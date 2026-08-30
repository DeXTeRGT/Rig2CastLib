using Rig2Cast.Abstractions.Events;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Runtime.Sessions;
using Rig2Cast.Simulator;

var driver = new SimulatedFtdx10Driver(new SimulatedRadioOptions
{
    CommandDelay = TimeSpan.FromMilliseconds(20)
});
await using ManagedRadio radio = await ManagedRadio.CreateAsync("demo-ftdx10", driver);
await using IRadioSession client = radio.OpenSession(
    new ClientIdentity("demo", "Rig2Cast demo"),
    ClientRole.Operator);

RadioSnapshot initial = await client.GetSnapshotAsync();
Console.WriteLine($"Connected to {initial.Capabilities.Manufacturer} {initial.Capabilities.Model}");
Console.WriteLine($"VFOs: {string.Join(", ", initial.Capabilities.Vfos.Available)}");

using var eventsStopping = new CancellationTokenSource();
Task eventPrinter = Task.Run(async () =>
{
    try
    {
        await foreach (RadioEvent radioEvent in client.WatchEventsAsync(eventsStopping.Token))
        {
            Console.WriteLine($"Event #{radioEvent.Sequence}: {radioEvent.Kind}");
        }
    }
    catch (OperationCanceledException) when (eventsStopping.IsCancellationRequested)
    {
    }
});

await client.SetFrequencyAsync(VfoId.A, 14_225_000);
await client.SetFrequencyAsync(VfoId.B, 7_125_000);
await client.SetModeAsync(RadioMode.Usb);
await client.SetSplitAsync(true);

LeaseToken transmitLease = await client.AcquireLeaseAsync(LeaseKinds.Transmit, TimeSpan.FromSeconds(2));
await client.SetPttAsync(true, transmitLease);
Console.WriteLine("Simulated PTT on");
await Task.Delay(150);
await client.SetPttAsync(false, transmitLease);
await client.ReleaseLeaseAsync(transmitLease);
Console.WriteLine("Simulated PTT off and lease released");

RadioSnapshot final = await client.GetSnapshotAsync();
Console.WriteLine($"VFO A: {final.State.FrequenciesHz[VfoId.A]:N0} Hz");
Console.WriteLine($"VFO B: {final.State.FrequenciesHz[VfoId.B]:N0} Hz");
Console.WriteLine($"Split: {final.State.IsSplit}");

await eventsStopping.CancelAsync();
await eventPrinter;
