using Rig2Cast.Runtime.Scheduling;

namespace Rig2Cast.Runtime.Tests;

public sealed class RadioCommandSchedulerTests
{
    [Fact]
    public async Task SafetyWorkRunsBeforeQueuedNormalWork()
    {
        await using var scheduler = new RadioCommandScheduler();
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        Task active = scheduler.ExecuteAsync(async _ =>
        {
            activeStarted.SetResult();
            await releaseActive.Task;
            order.Add("active");
        }).AsTask();
        await activeStarted.Task;

        Task normal = scheduler.ExecuteAsync(_ =>
        {
            order.Add("normal");
            return ValueTask.CompletedTask;
        }).AsTask();
        Task safety = scheduler.ExecuteAsync(_ =>
        {
            order.Add("safety");
            return ValueTask.CompletedTask;
        }, RadioCommandPriority.Safety).AsTask();

        releaseActive.SetResult();
        await Task.WhenAll(active, normal, safety);

        Assert.Equal(["active", "safety", "normal"], order);
    }
}
