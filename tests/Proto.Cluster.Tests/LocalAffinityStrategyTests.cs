﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClusterTest.Messages;
using FluentAssertions;
using Proto.Cluster.Cache;
using Proto.Remote;
using Proto.Remote.GrpcCore;
using Xunit;
using Xunit.Abstractions;

namespace Proto.Cluster.Tests
{
    public class LocalAffinityStrategyTests
        : ClusterTestBase,
            IClassFixture<LocalAffinityStrategyTests.LocalAffinityClusterFixture>
    {
        public LocalAffinityStrategyTests(
            ITestOutputHelper testOutputHelper,
            LocalAffinityClusterFixture clusterFixture
        ) : base(clusterFixture) => TestOutputHelper = testOutputHelper;

        private ITestOutputHelper TestOutputHelper { get; }

        [Fact]
        public async Task PrefersLocalPlacement()
        {
            await Task.Delay(3000);
            TestOutputHelper.WriteLine("Cluster ready");
            var timeout = new CancellationTokenSource(100_000).Token;

            var firstNode = Members[0];

            await PingAll(firstNode);
            await PingAll(firstNode);

            var secondNode = Members[1];
            firstNode.System.ProcessRegistry.ProcessCount.Should().BeGreaterThan(900,
                "We expect the actors to be localized to the node receiving traffic."
            );
            secondNode.System.ProcessRegistry.ProcessCount.Should().BeLessThan(100);

            TestOutputHelper.WriteLine(
                $"Actors: first node: {firstNode.System.ProcessRegistry.ProcessCount}, second node: {secondNode.System.ProcessRegistry.ProcessCount}"
            );
            var secondNodeTimings = Stopwatch.StartNew();
            await PingAll(secondNode);
            await PingAll(secondNode);
            await PingAll(secondNode);
            secondNodeTimings.Stop();

            TestOutputHelper.WriteLine("After traffic is shifted to second node:");
            TestOutputHelper.WriteLine(
                $"Actors: first node: {firstNode.System.ProcessRegistry.ProcessCount}, second node: {secondNode.System.ProcessRegistry.ProcessCount}"
            );

            firstNode.System.ProcessRegistry.ProcessCount.Should().BeInRange(100, 1000,
                "Some actors should have moved to the new node"
            );

            secondNode.System.ProcessRegistry.ProcessCount.Should().BeGreaterThan(100,
                "When traffic shifts to the second node, actors receiving remote traffic should start draining from the original node and be recreated"
            );

            secondNodeTimings.ElapsedMilliseconds.Should()
                .BeLessThan(3000, "We expect dead letter responses instead of timeouts");

            Task PingAll(Cluster cluster) => Task.WhenAll(
                Enumerable.Range(0, 1000).Select(async i => {
                        Pong pong = null;

                        while (pong is null)
                        {
                            timeout.ThrowIfCancellationRequested();
                            pong = await cluster.Ping(i.ToString(), "hello", timeout);
                        }
                    }
                )
            );
        }

        // ReSharper disable once ClassNeverInstantiated.Global
        public class LocalAffinityClusterFixture : BaseInMemoryClusterFixture
        {
            public LocalAffinityClusterFixture() : base(3,
                config => config
            )
            {
            }

            protected override ClusterKind[] ClusterKinds { get; } =
            {
                new(EchoActor.Kind, EchoActor.Props.WithPoisonOnRemoteTraffic(.5f).WithPidCacheInvalidation())
                    {StrategyBuilder = c => new LocalAffinityStrategy(c, 300)}
            };

            protected override async Task<Cluster> SpawnClusterMember(Func<ClusterConfig, ClusterConfig> configure)
            {
                var config = ClusterConfig.Setup(
                        _clusterName,
                        GetClusterProvider(),
                        GetIdentityLookup(_clusterName)
                    )
                    .WithClusterKinds(ClusterKinds);

                config = configure?.Invoke(config) ?? config;
                var system = new ActorSystem();

                var remoteConfig = GrpcCoreRemoteConfig.BindToLocalhost().WithProtoMessages(MessagesReflection.Descriptor);
                var _ = new GrpcCoreRemote(system, remoteConfig);

                var cluster = new Cluster(system, config);
                cluster.WithPidCacheInvalidation();
                await cluster.StartMemberAsync();
                return cluster;
            }
        }
    }
}