// -----------------------------------------------------------------------
// <copyright file="ActorContext.cs" company="Asynkron AB">
//      Copyright (C) 2015-2020 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Proto.Future;
using Proto.Mailbox;

namespace Proto.Context
{
    public class ActorContext : IMessageInvoker, IContext, ISupervisor
    {
        private static readonly ImmutableHashSet<PID> EmptyChildren = ImmutableHashSet<PID>.Empty;
        private readonly Props _props;

        private ActorContextExtras? _extras;
        private readonly IMailbox _mailbox;
        private object? _messageOrEnvelope;
        private ContextState _state;

        public ActorContext(ActorSystem system, Props props, PID? parent, PID self, IMailbox mailbox)
        {
            System = system;
            _props = props;
            _mailbox = mailbox;

            //Parents are implicitly watching the child
            //The parent is not part of the Watchers set
            Parent = parent;
            Self = self;

            Actor = IncarnateActor();
        }

        private static ILogger Logger { get; } = Log.CreateLogger<ActorContext>();

        public ActorSystem System { get; }
        public CancellationToken CancellationToken => EnsureExtras().CancellationTokenSource.Token;
        IReadOnlyCollection<PID> IContext.Children => Children;

        public IActor? Actor { get; private set; }
        public PID? Parent { get; }
        public PID Self { get; }

        public object? Message => MessageEnvelope.UnwrapMessage(_messageOrEnvelope);

        public PID? Sender => MessageEnvelope.UnwrapSender(_messageOrEnvelope);

        public MessageHeader Headers => MessageEnvelope.UnwrapHeader(_messageOrEnvelope);

        public TimeSpan ReceiveTimeout { get; private set; }

        public void Stash()
        {
            if (_messageOrEnvelope is not null) EnsureExtras().Stash.Push(_messageOrEnvelope);
        }

        public void Respond(object message)
        {
            if (Sender is not null)
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("{Self} Responding to {Sender} with message {Message}", Self, Sender, message);
                }

                SendUserMessage(Sender, message);
            }
            else
                Logger.LogWarning("{Self} Tried to respond but sender is null, with message {Message}", Self, message);
        }

        public PID Spawn(Props props)
        {
            var id = System.ProcessRegistry.NextId();
            return SpawnNamed(props, id);
        }

        public PID SpawnPrefix(Props props, string prefix)
        {
            var name = prefix + System.ProcessRegistry.NextId();
            return SpawnNamed(props, name);
        }

        public PID SpawnNamed(Props props, string name)
        {
            if (props.GuardianStrategy is not null)
                throw new ArgumentException("Props used to spawn child cannot have GuardianStrategy.");

            var pid = props.Spawn(System, $"{Self.Id}/{name}", Self);
            EnsureExtras().AddChild(pid);

            return pid;
        }

        public void Watch(PID pid) => pid.SendSystemMessage(System, new Watch(Self));

        public void Unwatch(PID pid) => pid.SendSystemMessage(System, new Unwatch(Self));

        public void SetReceiveTimeout(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration must be greater than zero");

            if (duration == ReceiveTimeout) return;

            ReceiveTimeout = duration;

            EnsureExtras();
            _extras!.StopReceiveTimeoutTimer();

            if (_extras.ReceiveTimeoutTimer is null)
            {
                _extras.InitReceiveTimeoutTimer(
                    new Timer(
                        ReceiveTimeoutCallback!, null!, ReceiveTimeout,
                        ReceiveTimeout
                    )
                );
            }
            else
                _extras.ResetReceiveTimeoutTimer(ReceiveTimeout);
        }

        public void CancelReceiveTimeout()
        {
            if (_extras?.ReceiveTimeoutTimer is null) return;

            _extras.StopReceiveTimeoutTimer();
            _extras.KillReceiveTimeoutTimer();

            ReceiveTimeout = TimeSpan.Zero;
        }

        public void Send(PID target, object message) => SendUserMessage(target, message);

        public void Forward(PID target)
        {
            switch (_messageOrEnvelope)
            {
                case null:
                    Logger.LogWarning("Message is null.");
                    return;
                case SystemMessage _:
                    Logger.LogWarning("SystemMessage cannot be forwarded. {Message}", _messageOrEnvelope);
                    return;
                default:
                    SendUserMessage(target, _messageOrEnvelope);
                    break;
            }
        }

        public void Request(PID target, object message)
        {
            var messageEnvelope = new MessageEnvelope(message, Self);
            SendUserMessage(target, messageEnvelope);
        }

        public void Request(PID target, object message, PID? sender)
        {
            var messageEnvelope = new MessageEnvelope(message, sender);
            SendUserMessage(target, messageEnvelope);
        }

        public Task<T> RequestAsync<T>(PID target, object message, TimeSpan timeout)
            => RequestAsync<T>(target, message, new FutureProcess(System, timeout));

        public Task<T> RequestAsync<T>(PID target, object message, CancellationToken cancellationToken)
            => RequestAsync<T>(target, message, new FutureProcess(System, cancellationToken));

        public Task<T> RequestAsync<T>(PID target, object message) =>
            RequestAsync<T>(target, message, new FutureProcess(System));

        public void ReenterAfter<T>(Task<T> target, Func<Task<T>, Task> action)
        {
            var msg = _messageOrEnvelope;
            var cont = new Continuation(() => action(target), msg);

            ScheduleContinuation(target, cont);
        }

        public void ReenterAfter(Task target, Action action)
        {
            var msg = _messageOrEnvelope;

            var cont = new Continuation(
                () => {
                    action();
                    return Task.CompletedTask;
                }, msg
            );

            ScheduleContinuation(target, cont);
        }

        public Task Receive(MessageEnvelope envelope)
        {
            _messageOrEnvelope = envelope;
            return DefaultReceive();
        }

        public void Stop(PID pid)
        {
            if (!System.Metrics.IsNoop)
            {
                System.Metrics.InternalActorMetrics.ActorStoppedCount.Inc(new[] {System.Id, System.Address, Actor!.GetType().Name});
            }

            pid.Stop(System);
        }

        public Task StopAsync(PID pid)
        {
            var future = new FutureProcess(System);

            pid.SendSystemMessage(System, new Watch(future.Pid));
            Stop(pid);

            return future.Task;
        }

        public void Poison(PID pid) => pid.SendUserMessage(System, PoisonPill.Instance);

        public Task PoisonAsync(PID pid) => RequestAsync<Terminated>(pid, PoisonPill.Instance, CancellationToken.None);

        public CancellationTokenSource? CancellationTokenSource => _extras?.CancellationTokenSource;

        public void EscalateFailure(Exception reason, object? message)
        {
            if (System.Config.DeveloperSupervisionLogging)
            {
                Console.WriteLine($"[Supervision] Actor {Self} : {Actor?.GetType().Name} failed with message:{message} exception:{reason}");
                Logger.LogError("[Supervision] Actor {Self} : {ActorType} failed with message:{Message} exception:{Reason}",Self,Actor?.GetType().Name,message,reason);
            }    
            
            System.Metrics.InternalActorMetrics.ActorFailureCount.Inc(new[] {System.Id, System.Address, Actor!.GetType().Name});
            var failure = new Failure(Self, reason, EnsureExtras().RestartStatistics, message);
            Self.SendSystemMessage(System, SuspendMailbox.Instance);

            if (Parent is null)
                HandleRootFailure(failure);
            else
                Parent.SendSystemMessage(System, failure);
        }

        public ValueTask InvokeSystemMessageAsync(object msg)
        {
            try
            {
                return msg switch
                {
                    Started s                       => InvokeUserMessageAsync(s),
                    Stop _                          => HandleStopAsync(),
                    Terminated t                    => HandleTerminatedAsync(t),
                    Watch w                         => HandleWatch(w),
                    Unwatch uw                      => HandleUnwatch(uw),
                    Failure f                       => HandleFailureAsync(f),
                    Restart                         => HandleRestartAsync(),
                    SuspendMailbox or ResumeMailbox => default,
                    Continuation cont               => HandleContinuation(cont),
                    _                               => HandleUnknownSystemMessage(msg)
                };
            }
            catch (Exception x)
            {
                Logger.LogError(x, "Error handling SystemMessage {Message}", msg);
                throw;
            }
        }

        public ValueTask InvokeUserMessageAsync(object msg)
        {
            if (System.Metrics.IsNoop)
            {
                return InternalInvokeUserMessageAsync(msg);
            }

            return Await(this, msg);
            
            //static, don't create a closure
            static async ValueTask Await(ActorContext self, object msg)
            {
                self.System.Metrics.InternalActorMetrics.ActorMailboxLength.Set(self._mailbox.UserMessageCount,
                    new[] {self.System.Id, self.System.Address, self.Actor!.GetType().Name}
                );

                var sw = Stopwatch.StartNew();
                await self.InternalInvokeUserMessageAsync(msg);
                sw.Stop();
                self.System.Metrics.InternalActorMetrics.ActorMessageReceiveHistogram.Observe(sw,
                    new[] {self.System.Id, self.System.Address, self.Actor!.GetType().Name, MessageEnvelope.UnwrapMessage(msg)!.GetType().Name}
                );
            }
        }

        public IImmutableSet<PID> Children => _extras?.Children ?? EmptyChildren;

        public void RestartChildren(Exception reason, params PID[] pids) =>
            pids.SendSystemMessage(new Restart(reason), System);

        public void StopChildren(params PID[] pids) => pids.SendSystemMessage(Proto.Stop.Instance, System);

        public void ResumeChildren(params PID[] pids) => pids.SendSystemMessage(ResumeMailbox.Instance, System);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ValueTask InternalInvokeUserMessageAsync(object msg)
        {
            if (_state == ContextState.Stopped)
            {
                //already stopped, send message to deadletter process
                System.DeadLetter.SendUserMessage(Self, msg);
                return default;
            }

            if (ReceiveTimeout > TimeSpan.Zero)
            {
                var notInfluenceTimeout = msg is INotInfluenceReceiveTimeout;
                var influenceTimeout = !notInfluenceTimeout;

                if (influenceTimeout) _extras?.StopReceiveTimeoutTimer();
            }

            Task t;

            //slow path, there is middleware, message must be wrapped in an envelope
            if (_props.ReceiverMiddlewareChain is not null)
                t = _props.ReceiverMiddlewareChain(EnsureExtras().Context, MessageEnvelope.Wrap(msg));
            else
            {
                if (_props.ContextDecoratorChain is not null)
                    t = EnsureExtras().Context.Receive(MessageEnvelope.Wrap(msg));
                else
                {
                    _messageOrEnvelope = msg;
                    t = DefaultReceive();
                }

                //fast path, 0 alloc invocation of actor receive
            }

            if (t.IsCompleted)
            {
                _extras?.ResetReceiveTimeoutTimer(ReceiveTimeout);
                return default;
            }

            return Await(this, t);

            //static, dont create closure
            static async ValueTask Await(ActorContext self, Task t)
            {
                await t;
                self._extras?.ResetReceiveTimeoutTimer(self.ReceiveTimeout);
            }
        }

        public static ActorContext Setup(ActorSystem system, Props props, PID? parent, PID self, IMailbox mailbox) =>
            new(system, props, parent, self, mailbox);

        private void ScheduleContinuation(Task target, Continuation cont) =>
            _ = SafeTask.Run(async () => {
                    await target;
                    Self.SendSystemMessage(System, cont);
                }
                , CancellationToken.None
            );

        private static ValueTask HandleUnknownSystemMessage(object msg)
        {
            //TODO: sounds like a pretty severe issue if we end up here? what todo?
            Logger.LogWarning("Unknown system message {Message}", msg);
            return default;
        }

        private async ValueTask HandleContinuation(Continuation cont)
        {
            _messageOrEnvelope = cont.Message;
            await cont.Action();
        }

        private ActorContextExtras EnsureExtras()
        {
            if (_extras is not null) return _extras;

            var context = _props.ContextDecoratorChain?.Invoke(this) ?? this;
            _extras = new ActorContextExtras(context);

            return _extras;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Task DefaultReceive() =>
            Message switch
            {
                PoisonPill => HandlePoisonPill(),
                _          => Actor!.ReceiveAsync(_props.ContextDecoratorChain is not null ? EnsureExtras().Context : this)
            };

        private Task HandlePoisonPill()
        {
            if (Sender != null) HandleWatch(new Watch(Sender));

            Stop(Self);
            return Task.CompletedTask;
        }

        private async Task<T> RequestAsync<T>(PID target, object message, FutureProcess future)
        {
            var messageEnvelope = new MessageEnvelope(message, future.Pid);
            SendUserMessage(target, messageEnvelope);
            var result = await future.Task;

            switch (result)
            {
                case DeadLetterResponse:
                    throw new DeadLetterException(target);
                case null:
                case T:
                    return (T) result!;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected message. Was type {result?.GetType()} but expected {typeof(T)}"
                    );
            }
        }
        
        private void SendUserMessage(PID target, object message)
        {
            if (_props.SenderMiddlewareChain is null)
            {
                //fast path, 0 alloc
                target.SendUserMessage(System, message);
            }
            else
            {
                //slow path
                _props.SenderMiddlewareChain(EnsureExtras().Context, target, MessageEnvelope.Wrap(message));
            }
        }
        
        private IActor IncarnateActor()
        {
            _state = ContextState.Alive;
            var actor = _props.Producer(System);

            if (!System.Metrics.IsNoop)
            {
                System.Metrics.InternalActorMetrics.ActorSpawnCount.Inc(new[] {System.Id, System.Address, actor.GetType().Name});
            }

            return actor;
        }

        
        private async ValueTask HandleRestartAsync()
        {
            _state = ContextState.Restarting;
            CancelReceiveTimeout();
            await InvokeUserMessageAsync(Restarting.Instance);
            await StopAllChildren();

            if (!System.Metrics.IsNoop)
            {
                System.Metrics.InternalActorMetrics.ActorRestartedCount.Inc(new[] {System.Id, System.Address, Actor!.GetType().Name});
            }
        }

        private ValueTask HandleUnwatch(Unwatch uw)
        {
            _extras?.Unwatch(uw.Watcher);
            return default;
        }

        private ValueTask HandleWatch(Watch w)
        {
            if (_state >= ContextState.Stopping)
                w.Watcher.SendSystemMessage(System, Terminated.From(Self, TerminatedReason.Stopped));
            else
                EnsureExtras().Watch(w.Watcher);

            return default;
        }

        private ValueTask HandleFailureAsync(Failure msg)
        {
            switch (Actor)
            {
                case ISupervisorStrategy supervisor:
                    supervisor.HandleFailure(this, msg.Who, msg.RestartStatistics, msg.Reason, msg.Message);
                    break;
                default:
                    _props.SupervisorStrategy.HandleFailure(
                        this, msg.Who, msg.RestartStatistics, msg.Reason,
                        msg.Message
                    );
                    break;
            }

            return default;
        }

        // this will be triggered by the actors own Termination, _and_ terminating direct children, or Watchees
        private async ValueTask HandleTerminatedAsync(Terminated msg)
        {
            //In the case of a Watchee terminating, this will have no effect, except that the terminate message is
            //passed onto the user message Receive for user level handling
            _extras?.RemoveChild(msg.Who);
            await InvokeUserMessageAsync(msg);

            if (_state is ContextState.Stopping or ContextState.Restarting) await TryRestartOrStopAsync();
        }

        private void HandleRootFailure(Failure failure)
            => Supervision.DefaultStrategy.HandleFailure(
                this, failure.Who, failure.RestartStatistics, failure.Reason,
                failure.Message
            );

        //Initiate stopping, not final
        private ValueTask HandleStopAsync()
        {
            if (_state >= ContextState.Stopping)
            {
                //already stopping or stopped
                return default;
            }

            _state = ContextState.Stopping;
            CancelReceiveTimeout();

            return Await(this);

            static async ValueTask Await(ActorContext self)
            {
                await self.InvokeUserMessageAsync(Stopping.Instance);
                await self.StopAllChildren();
            }
        }

        private ValueTask StopAllChildren()
        {
            _extras?.Children.Stop(System);

            return TryRestartOrStopAsync();
        }

        //intermediate stopping stage, waiting for children to stop
        //this is directly triggered by StopAllChildren, or by Terminated messages from stopping children
        private ValueTask TryRestartOrStopAsync()
        {
            if (_extras?.Children.Count > 0) return default;

            CancelReceiveTimeout();
            
            //all children are now stopped, should we restart or stop ourselves?
            switch (_state)
            {
                case ContextState.Restarting:
                    return RestartAsync();
                case ContextState.Stopping:
                    return FinalizeStopAsync();
                default:
                    return default;
            }
        }

        //Last and final termination step
        private async ValueTask FinalizeStopAsync()
        {
            System.ProcessRegistry.Remove(Self);
            //This is intentional
            await InvokeUserMessageAsync(Stopped.Instance);

            await DisposeActorIfDisposable();

            //Notify watchers
            _extras?.Watchers.SendSystemMessage(Terminated.From(Self, TerminatedReason.Stopped), System);

            //Notify parent
            Parent?.SendSystemMessage(System, Terminated.From(Self, TerminatedReason.Stopped));

            _state = ContextState.Stopped;
        }

        private async ValueTask RestartAsync()
        {
            await DisposeActorIfDisposable();
            Actor = IncarnateActor();
            Self.SendSystemMessage(System, ResumeMailbox.Instance);

            await InvokeUserMessageAsync(Started.Instance);

            if (_extras?.Stash is not null)
            {
                var currentStash = new Stack<object>(_extras.Stash);
                _extras.Stash.Clear();

                //TODO: what happens if we hit a failure here?
                while (currentStash.Any())
                {
                    var msg = currentStash.Pop();
                    await InvokeUserMessageAsync(msg);
                }
            }
        }

        private ValueTask DisposeActorIfDisposable()
        {
            switch (Actor)
            {
                case IAsyncDisposable asyncDisposableActor:
                    return asyncDisposableActor.DisposeAsync();
                case IDisposable disposableActor:
                    disposableActor.Dispose();
                    break;
            }

            return default;
        }

        private void ReceiveTimeoutCallback(object state)
        {
            if (_extras?.ReceiveTimeoutTimer is null) return;

            SendUserMessage(Self, Proto.ReceiveTimeout.Instance);
        }
    }
}