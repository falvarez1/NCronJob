using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace NCronJob.Tests;

public sealed class NCronJobIntegrationTests : JobIntegrationBase
{
    [Fact]
    public async Task CronJobThatIsScheduledEveryMinuteShouldBeExecuted()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtEveryMinute)));
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(1);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task AdvancingTheWholeTimeShouldHaveTenEntries()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtEveryMinute)));
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        void AdvanceTime() => FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(10, AdvanceTime);

        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task JobsShouldCancelOnCancellation()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtEveryMinute)));
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        var jobFinished = await DoNotWaitJustCancel(10);
        jobFinished.ShouldBeFalse();
    }

    [Fact]
    public async Task EachJobRunHasItsOwnScope()
    {
        var storage = new Storage();
        ServiceCollection.AddSingleton(storage);
        ServiceCollection.AddScoped<GuidGenerator>();
        ServiceCollection.AddNCronJob(n => n.AddJob<ScopedServiceJob>(
            p => p.WithCronExpression(Cron.AtEveryMinute).WithParameter("null")
                .And
                .WithCronExpression(Cron.AtEveryMinute)));
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        FakeTimer.Advance(TimeSpan.FromMinutes(1));

        await Task.WhenAll(GetCompletionJobs(2, CancellationToken));

        storage.Entries.Distinct().Count().ShouldBe(storage.Entries.Count);
        storage.Entries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAnInstantJobWithoutPreviousRegistration()
    {
        ServiceCollection.AddNCronJob();

        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        provider.GetRequiredService<IInstantJobRegistry>().RunInstantJob<SimpleJob>(token: CancellationToken);

        var jobFinished = await WaitForJobsOrTimeout(1);
        jobFinished.ShouldBeTrue();

        var jobRegistry = provider.GetRequiredService<JobRegistry>();
        jobRegistry.FindFirstJobDefinition(typeof(SimpleJob)).ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAnInstantJob()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>());
        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        provider.GetRequiredService<IInstantJobRegistry>().RunInstantJob<SimpleJob>(token: CancellationToken);

        var jobFinished = await WaitForJobsOrTimeout(1);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task InstantJobShouldInheritInitiallyDefinedParameter()
    {
        ServiceCollection.AddNCronJob(
            n => n.AddJob<ParameterJob>(o => o.WithCronExpression(Cron.Never).WithParameter("Hello from AddNCronJob")));

        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        provider.GetRequiredService<IInstantJobRegistry>().RunInstantJob<ParameterJob>(token: CancellationToken);

        var content = await CommunicationChannel.Reader.ReadAsync(CancellationToken);
        content.ShouldBe("Hello from AddNCronJob");
    }

    [Fact]
    public async Task InstantJobCanOverrideInitiallyDefinedParameter()
    {
        ServiceCollection.AddNCronJob(
            n => n.AddJob<ParameterJob>(o => o.WithCronExpression(Cron.Never).WithParameter("Hello from AddNCronJob")));

        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        provider.GetRequiredService<IInstantJobRegistry>().RunInstantJob<ParameterJob>("Hello from InstantJob", CancellationToken);

        var content = await CommunicationChannel.Reader.ReadAsync(CancellationToken);
        content.ShouldBe("Hello from InstantJob");
    }

    [Fact]
    public async Task InstantJobShouldPassDownParameter()
    {
        ServiceCollection.AddNCronJob(
            n => n.AddJob<ParameterJob>());

        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        provider.GetRequiredService<IInstantJobRegistry>().RunInstantJob<ParameterJob>("Hello from InstantJob", CancellationToken);

        var content = await CommunicationChannel.Reader.ReadAsync(CancellationToken);
        content.ShouldBe("Hello from InstantJob");
    }

    [Theory]
    [MemberData(nameof(InstantJobRunners))]
    public async Task InstantJobCanStartADisabledJob(Func<IInstantJobRegistry, string, CancellationToken, Guid> instantJobRunner)
    {
        ServiceCollection.AddNCronJob(
            n => n.AddJob<ParameterJob>((jo) => jo.WithCronExpression(Cron.AtEveryMinute)));

        var provider = CreateServiceProvider();

        (IDisposable subscription, IList<ExecutionProgress> events) = RegisterAnExecutionProgressSubscriber(provider);

        var registry = provider.GetRequiredService<IRuntimeJobRegistry>();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        registry.DisableJob<ParameterJob>();

        var instantJobRegistry = provider.GetRequiredService<IInstantJobRegistry>();
        var orchestrationId = instantJobRunner(instantJobRegistry, "Hello from InstantJob", CancellationToken);

        await WaitForOrchestrationCompletion(events, orchestrationId);

        subscription.Dispose();

        Assert.Equal(ExecutionState.OrchestrationStarted, events[0].State);
        Assert.Equal(ExecutionState.NotStarted, events[1].State);
        Assert.Equal(ExecutionState.Scheduled, events[2].State);
        Assert.Equal(ExecutionState.Cancelled, events[3].State);
        Assert.Equal(ExecutionState.OrchestrationCompleted, events[4].State);
        Assert.Equal(ExecutionState.OrchestrationStarted, events[5].State);
        Assert.Equal(ExecutionState.NotStarted, events[6].State);
        Assert.Equal(ExecutionState.Initializing, events[7].State);
        Assert.Equal(ExecutionState.Running, events[8].State);
        Assert.Equal(ExecutionState.Completing, events[9].State);
        Assert.Equal(ExecutionState.Completed, events[10].State);
        Assert.Equal(ExecutionState.OrchestrationCompleted, events[11].State);
        Assert.Equal(12, events.Count);
    }

    public static TheoryData<Func<IInstantJobRegistry, string, CancellationToken, Guid>> InstantJobRunners()
    {
        var t = new TheoryData<Func<IInstantJobRegistry, string, CancellationToken, Guid>>();
        t.Add((i, p, t) => i.RunInstantJob<ParameterJob>(p, t));
        t.Add((i, p, t) => i.ForceRunInstantJob<ParameterJob>(p, t));
        return t;
    }

    [Fact]
    public async Task CronJobShouldInheritInitiallyDefinedParameter()
    {
        ServiceCollection.AddNCronJob(
            n => n.AddJob<ParameterJob>(p => p.WithCronExpression(Cron.AtEveryMinute).WithParameter("Hello from AddNCronJob")));
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        FakeTimer.Advance(TimeSpan.FromMinutes(1));

        var content = await CommunicationChannel.Reader.ReadAsync(CancellationToken);
        content.ShouldBe("Hello from AddNCronJob");
    }

    [Fact]
    public async Task CronJobThatIsScheduledEverySecondShouldBeExecuted()
    {
        FakeTimer.Advance(TimeSpan.FromSeconds(1));
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtEverySecond)));
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        void AdvanceTime() => FakeTimer.Advance(TimeSpan.FromSeconds(1));
        var jobFinished = await WaitForJobsOrTimeout(10, AdvanceTime);

        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task CanRunSecondPrecisionAndMinutePrecisionJobs()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>(
            p => p.WithCronExpression(Cron.AtEverySecond).And.WithCronExpression(Cron.AtEveryMinute)));
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        void AdvanceTime() => FakeTimer.Advance(TimeSpan.FromSeconds(1));
        var jobFinished = await WaitForJobsOrTimeout(61, AdvanceTime);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task LongRunningJobShouldNotBlockScheduler()
    {
        ServiceCollection.AddNCronJob(n => n
                .AddJob<LongRunningJob>(p => p.WithCronExpression(Cron.AtEveryMinute))
                .AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtEveryMinute)));
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(1);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAScheduledJob()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>());
        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        provider.GetRequiredService<IInstantJobRegistry>().RunScheduledJob<SimpleJob>(TimeSpan.FromMinutes(1), token: CancellationToken);

        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(1);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAScheduledJobWithDateTimeOffset()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>());
        var provider = CreateServiceProvider();
        var runDate = FakeTimer.GetUtcNow().AddMinutes(1);
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        provider.GetRequiredService<IInstantJobRegistry>().RunScheduledJob<SimpleJob>(runDate, token: CancellationToken);

        await Task.Delay(10, CancellationToken);
        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(1);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAScheduledJobWithDateTimeOffsetInThePast()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>());
        var provider = CreateServiceProvider();
        var runDate = FakeTimer.GetUtcNow().AddDays(-1);

        (var subscription, IList<ExecutionProgress> events) = RegisterAnExecutionProgressSubscriber(provider);

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        var orchestrationId = provider.GetRequiredService<IInstantJobRegistry>().RunScheduledJob<SimpleJob>(runDate, token: CancellationToken);

        await Task.Delay(10, CancellationToken);
        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(1);
        jobFinished.ShouldBeFalse();

        await WaitForOrchestrationCompletion(events, orchestrationId);

        subscription.Dispose();

        Assert.All(events, e => Assert.Equal(orchestrationId, e.CorrelationId));
        Assert.Equal(ExecutionState.OrchestrationStarted, events[0].State);
        Assert.Equal(ExecutionState.NotStarted, events[1].State);
        Assert.Equal(ExecutionState.Expired, events[2].State);
        Assert.Equal(ExecutionState.OrchestrationCompleted, events[3].State);
        Assert.Equal(4, events.Count);
    }

    [Fact]
    public async Task WhileAwaitingJobTriggeringInstantJobShouldAnywayTriggerCronJob()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtMinute0)));
        var provider = CreateServiceProvider();

        (var subscription, IList<ExecutionProgress> events) = RegisterAnExecutionProgressSubscriber(provider);

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        var instantOrchestrationId = provider.GetRequiredService<IInstantJobRegistry>().RunInstantJob<SimpleJob>(token: CancellationToken);

        await WaitForOrchestrationCompletion(events, instantOrchestrationId);

        subscription.Dispose();

        var scheduledOrchestrationId = events.First().CorrelationId;

        var scheduledOrchestrationEvents = events.Where(e => e.CorrelationId == scheduledOrchestrationId).ToList();
        var instantOrchestrationEvents = events.Where(e => e.CorrelationId == instantOrchestrationId).ToList();

        Assert.Equal(ExecutionState.OrchestrationStarted, scheduledOrchestrationEvents[0].State);
        Assert.Equal(ExecutionState.NotStarted, scheduledOrchestrationEvents[1].State);
        Assert.Equal(ExecutionState.Scheduled, scheduledOrchestrationEvents[2].State);
        Assert.Equal(ExecutionState.Cancelled, scheduledOrchestrationEvents[3].State);
        Assert.Equal(ExecutionState.OrchestrationCompleted, scheduledOrchestrationEvents[4].State);
        Assert.Equal(5, scheduledOrchestrationEvents.Count);

        Assert.Equal(ExecutionState.OrchestrationStarted, instantOrchestrationEvents[0].State);
        Assert.Equal(ExecutionState.NotStarted, instantOrchestrationEvents[1].State);
        Assert.Equal(ExecutionState.Initializing, instantOrchestrationEvents[2].State);
        Assert.Equal(ExecutionState.Running, instantOrchestrationEvents[3].State);
        Assert.Equal(ExecutionState.Completing, instantOrchestrationEvents[4].State);
        Assert.Equal(ExecutionState.Completed, instantOrchestrationEvents[5].State);
        Assert.Equal(ExecutionState.OrchestrationCompleted, instantOrchestrationEvents[6].State);
        Assert.Equal(7, instantOrchestrationEvents.Count);

        // Scheduled orchestration should have started before the instant job related one...
        Assert.True(scheduledOrchestrationEvents[0].Timestamp < instantOrchestrationEvents[0].Timestamp);

        // ...and cancelled before the initialization of the instant job related one.
        Assert.True(scheduledOrchestrationEvents[3].Timestamp < instantOrchestrationEvents[2].Timestamp);
    }

    [Fact]
    public async Task MinimalJobApiCanBeUsedForTriggeringCronJobs()
    {
        ServiceCollection.AddNCronJob(async (ChannelWriter<object?> writer) =>
        {
            await writer.WriteAsync(null, CancellationToken);
        }, Cron.AtEveryMinute);
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(1);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task ConcurrentJobConfigurationShouldBeRespected()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<ShortRunningJob>(p => p
            .WithCronExpression(Cron.AtEveryMinute).WithName("Job 1")
            .And.WithCronExpression(Cron.AtEveryMinute).WithName("Job 2")
            .And.WithCronExpression(Cron.AtEveryMinute).WithName("Job 3")
            .And.WithCronExpression(Cron.AtEveryMinute).WithName("Job 4")));

        var provider = CreateServiceProvider();

        (var subscription, IList<ExecutionProgress> events) = RegisterAnExecutionProgressSubscriber(provider);

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        FakeTimer.Advance(TimeSpan.FromMinutes(1));

        await WaitUntilConditionIsMet(events, AtLeastTwoJobsAreInitializing);

        subscription.Dispose();

        var runningJobs = events.Where(e => e.State == ExecutionState.Initializing).ToList();

        Assert.Equal(2, runningJobs.Count);
        Assert.NotEqual(runningJobs[0].CorrelationId, runningJobs[1].CorrelationId);

        static bool AtLeastTwoJobsAreInitializing(IList<ExecutionProgress> events)
        {
            var jobs = events.Where(e => e.State == ExecutionState.Initializing).ToList();
            return jobs.Count >= 2;
        }
    }

    [Fact]
    public async Task InstantJobHasHigherPriorityThanCronJob()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<ParameterJob>(p => p.WithCronExpression(Cron.AtEveryMinute).WithParameter("CRON")));
        ServiceCollection.AddSingleton(_ => new ConcurrencySettings { MaxDegreeOfParallelism = 1 });
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        provider.GetRequiredService<IInstantJobRegistry>().RunInstantJob<ParameterJob>("INSTANT", CancellationToken);
        FakeTimer.Advance(TimeSpan.FromMinutes(1));

        var answer = await CommunicationChannel.Reader.ReadAsync(CancellationToken);
        answer.ShouldBe("INSTANT");
    }

    [Fact]
    public async Task TriggeringInstantJobWithoutRegisteringContinuesToWork()
    {
        ServiceCollection.AddNCronJob();
        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        Action act = () => provider.GetRequiredService<IInstantJobRegistry>().RunInstantJob<SimpleJob>(token: CancellationToken);

        act.ShouldNotThrow();
    }

    [Fact]
    public async Task ExecuteAnInstantJobDelegate()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>());
        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        provider.GetRequiredService<IInstantJobRegistry>().RunInstantJob(async (ChannelWriter<object> writer) =>
        {
            await writer.WriteAsync("Done", CancellationToken);
        }, CancellationToken);

        var jobFinished = await WaitForJobsOrTimeout(1);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task AnonymousJobsCanBeExecutedMultipleTimes()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob(async (ChannelWriter<object> writer, CancellationToken ct) =>
        {
            await Task.Delay(10, ct);
            await writer.WriteAsync(true, ct);
        }, Cron.AtEveryMinute));
        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);
        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        await WaitForJobsOrTimeout(1);

        void AdvanceTime() => FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(1, AdvanceTime);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task StaticAnonymousJobsCanBeExecutedMultipleTimes()
    {

        ServiceCollection.AddNCronJob(n => n.AddJob(JobMethods.WriteTrueStaticAsync, Cron.AtEveryMinute));

        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);
        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        await WaitForJobsOrTimeout(1);

        void AdvanceTime() => FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(1, AdvanceTime);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public void AddingJobsWithTheSameCustomNameLeadsToException()
    {
        Action act = () => ServiceCollection.AddNCronJob(
            n => n.AddJob(() => { }, Cron.AtEveryMinute, jobName: "Job1")
                .AddJob(() => { }, Cron.AtMinute0, jobName: "Job1"));

        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void AddJobsDynamicallyWhenNameIsDuplicatedLeadsToException()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob(() => { }, Cron.AtEveryMinute, jobName: "Job1"));
        var provider = CreateServiceProvider();
        var runtimeRegistry = provider.GetRequiredService<IRuntimeJobRegistry>();

        var successful = runtimeRegistry.TryRegister(n => n.AddJob(() => { }, Cron.AtEveryMinute, jobName: "Job1"), out var exception);

        successful.ShouldBeFalse();
        exception.ShouldNotBeNull();
    }

    [Fact]
    public async Task TwoJobsWithDifferentDefinitionLeadToTwoExecutions()
    {
        ServiceCollection.AddNCronJob(n => n
            .AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtEveryMinute).WithParameter("1"))
            .AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtEveryMinute).WithParameter("2")));
        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        var countJobs = provider.GetRequiredService<JobRegistry>().GetAllCronJobs().Count;
        countJobs.ShouldBe(2);

        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(2, TimeSpan.FromMilliseconds(250));
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    [SuppressMessage("Usage", "CA2263: Prefer generic overload", Justification = "Needed for the test")]
    public async Task AddJobWithTypeAsParameterAddsJobs()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob(typeof(SimpleJob), p => p.WithCronExpression(Cron.AtEveryMinute)));
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(1);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task AddingJobsAndDuringStartupAndRuntimeNotInOnlyOneCallOnlyOneExecution()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>().AddJob<ShortRunningJob>());
        var provider = CreateServiceProvider();
        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);
        var registry = provider.GetRequiredService<IRuntimeJobRegistry>();

        registry.TryRegister(n => n.AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtEveryMinute)));
        registry.TryRegister(n => n.AddJob<ShortRunningJob>(p => p.WithCronExpression("0 0 10 * *")));

        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(1);
        jobFinished.ShouldBeTrue();
        var anotherJobFinished = await WaitForJobsOrTimeout(1, TimeSpan.FromMilliseconds(500));
        anotherJobFinished.ShouldBeFalse();
    }

    [Fact]
    public async Task CallingAddNCronJobMultipleTimesWillRegisterAllJobs()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<ShortRunningJob>(p => p.WithCronExpression(Cron.AtEveryMinute)));
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtEveryMinute)));
        var provider = CreateServiceProvider();

        await provider.GetRequiredService<IHostedService>().StartAsync(CancellationToken);

        FakeTimer.Advance(TimeSpan.FromMinutes(1));
        var jobFinished = await WaitForJobsOrTimeout(2);
        jobFinished.ShouldBeTrue();
    }

    [Fact]
    public void RegisteringDuplicatedJobsLeadToAnExceptionWhenRegistration()
    {
        Action act = () =>
            ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>(p => p
                .WithCronExpression(Cron.AtEveryMinute)
                .And
                .WithCronExpression(Cron.AtEveryMinute)));

        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void RegisteringDuplicateDuringRuntimeLeadsToException()
    {
        ServiceCollection.AddNCronJob(n => n.AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtEveryMinute)));
        var provider = CreateServiceProvider();
        var runtimeRegistry = provider.GetRequiredService<IRuntimeJobRegistry>();

        var successful = runtimeRegistry.TryRegister(n => n.AddJob<SimpleJob>(p => p.WithCronExpression(Cron.AtEveryMinute)), out var exception);

        successful.ShouldBeFalse();
        exception.ShouldNotBeNull();
    }

    private static class JobMethods
    {
        public static async Task WriteTrueStaticAsync(ChannelWriter<object> writer, CancellationToken ct)
        {
            await Task.Delay(10, ct);
            await writer.WriteAsync(true, ct);
        }
    }

    private sealed class GuidGenerator
    {
        public Guid NewGuid { get; } = Guid.NewGuid();
    }

    [SupportsConcurrency(2)]
    private sealed class SimpleJob(ChannelWriter<object> writer) : IJob
    {
        public async Task RunAsync(IJobExecutionContext context, CancellationToken token)
        {
            try
            {
                context.Output = "Job Completed";
                await Task.Delay(10, token);
                await writer.WriteAsync(context.Output, token);
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(ex, token);
            }
        }
    }

    [SupportsConcurrency(2)]
    private sealed class ShortRunningJob(ChannelWriter<object> writer) : IJob
    {
        public async Task RunAsync(IJobExecutionContext context, CancellationToken token)
        {
            try
            {
                context.Output = "Job Completed";
                await writer.WriteAsync(context.Output, token);
                await Task.Delay(200, token);
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(ex, token);
            }
        }
    }

    private sealed class LongRunningJob(TimeProvider timeProvider) : IJob
    {
        public async Task RunAsync(IJobExecutionContext context, CancellationToken token) =>
            await Task.Delay(TimeSpan.FromSeconds(10), timeProvider, token);
    }

    private sealed class ScopedServiceJob(ChannelWriter<object> writer, Storage storage, GuidGenerator guidGenerator) : IJob
    {
        public async Task RunAsync(IJobExecutionContext context, CancellationToken token)
        {
            storage.Entries.Add(guidGenerator.NewGuid.ToString());
            await writer.WriteAsync(true, token);
        }
    }

    private sealed class ParameterJob(ChannelWriter<object> writer) : IJob
    {
        public async Task RunAsync(IJobExecutionContext context, CancellationToken token)
            => await writer.WriteAsync(context.Parameter!, token);
    }
}
