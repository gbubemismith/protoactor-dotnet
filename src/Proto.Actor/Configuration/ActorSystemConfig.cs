// -----------------------------------------------------------------------
// <copyright file="ActorSystemConfig.cs" company="Asynkron AB">
//      Copyright (C) 2015-2020 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------
using System;
using JetBrains.Annotations;
using Ubiquitous.Metrics;
using Ubiquitous.Metrics.NoMetrics;

// -----------------------------------------------------------------------
//   <copyright file="ActorContext.cs" company="Asynkron AB">
//       Copyright (C) 2015-2020 Asynkron AB All rights reserved
//   </copyright>
// -----------------------------------------------------------------------

// ReSharper disable once CheckNamespace
namespace Proto
{
    [PublicAPI]
    public record ActorSystemConfig
    {
        public TimeSpan DeadLetterThrottleInterval { get; init; }

        public IMetricsProvider[] MetricsProviders { get; init; } = {new NoMetricsProvider()};
        public int DeadLetterThrottleCount { get; init; }

        public bool DeadLetterRequestLogging { get; set; } = true;
        public bool DeveloperSupervisionLogging { get; init; }

        public static ActorSystemConfig Setup() => new();

        public ActorSystemConfig WithDeadLetterThrottleInterval(TimeSpan deadLetterThrottleInterval) =>
            this with {DeadLetterThrottleInterval = deadLetterThrottleInterval};

        public ActorSystemConfig WithDeadLetterThrottleCount(int deadLetterThrottleCount) =>
            this with {DeadLetterThrottleCount = deadLetterThrottleCount};

        public ActorSystemConfig WithDeadLetterRequestLogging(bool enabled) => this with {DeadLetterRequestLogging = enabled};

        public ActorSystemConfig WithDeveloperSupervisionLogging(bool enabled) => this with {DeveloperSupervisionLogging = enabled};

        public ActorSystemConfig WithMetricsProviders(params IMetricsProvider[] providers) => this with {MetricsProviders = providers};
    }
}