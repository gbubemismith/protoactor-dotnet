using System.Threading.Tasks;
using ClusterTest.Messages;
using Microsoft.Extensions.Logging;

namespace Proto.Cluster.Tests
{
    public class EchoActor : IActor
    {
        public const string Kind = "echo";
        public const string Kind2 = "echo2";

        public static readonly Props Props = Props.FromProducer(() => new EchoActor());
        private static readonly ILogger Logger = Log.CreateLogger<EchoActor>();

        private string _identity;
        private string _initKind;

        public async Task ReceiveAsync(IContext context)
        {
            switch (context.Message)
            {
                case Started _:
                    Logger.LogDebug("{Context}", context.Self);
                    break;
                case ClusterInit init:
                    _identity = init.Identity;
                    _initKind = init.Kind;
                    break;
                case Ping ping:
                    var pong = new Pong {Message = ping.Message, Kind = _initKind ?? "", Identity = _identity ?? ""};
                    Logger.LogDebug("Received Ping, replying Pong: {@Pong}", pong);
                    context.Respond(pong);
                    break;
                case SlowPing ping:
                    await Task.Delay(ping.DelayMs);
                    var slowPong = new Pong {Message = ping.Message, Kind = _initKind ?? "", Identity = _identity ?? ""};
                    Logger.LogDebug("Received SlowPing, replying Pong after {Delay} ms: {@Pong}", ping.DelayMs, slowPong);
                    context.Respond(slowPong);
                    break;
                case WhereAreYou _:
                    Logger.LogDebug("Responding to location request");
                    context.Respond(new HereIAm {Address = context.Self!.Address});
                    break;
                case Die _:
                    Logger.LogDebug("Received termination request, stopping");
                    context.Respond(new Ack());
                    context.Stop(context.Self!);
                    break;
                default:
                    Logger.LogDebug(context.Message?.GetType().Name);
                    break;
            }
        }
    }
}