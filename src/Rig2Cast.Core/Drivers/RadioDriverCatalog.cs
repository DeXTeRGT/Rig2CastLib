using Rig2Cast.Abstractions.Drivers;

namespace Rig2Cast.Core.Drivers;

public sealed record RadioModelRegistration(
    RadioModelDescriptor Model,
    RadioDriverDescriptor Driver,
    IRadioDriverFactory Factory);

public sealed class RadioDriverCatalog
{
    private readonly Dictionary<string, RadioModelRegistration> _models =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RadioModelRegistration> Models => _models.Values
        .OrderBy(item => item.Model.Manufacturer, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Model.Model, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void Register(IRadioDriverFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ValidateDriver(factory.Descriptor);
        foreach (RadioModelDescriptor model in factory.Descriptor.Models)
        {
            if (_models.ContainsKey(model.Id))
                throw new InvalidOperationException($"Radio model ID '{model.Id}' is already registered.");
        }

        foreach (RadioModelDescriptor model in factory.Descriptor.Models)
            _models.Add(model.Id, new RadioModelRegistration(model, factory.Descriptor, factory));
    }

    public bool TryFind(string modelId, out RadioModelRegistration? registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return _models.TryGetValue(modelId, out registration);
    }

    public RadioModelRegistration Find(string modelId) =>
        TryFind(modelId, out RadioModelRegistration? registration)
            ? registration!
            : throw new KeyNotFoundException($"Radio model '{modelId}' is not registered. Use --list-models to see available models.");

    private static void ValidateDriver(RadioDriverDescriptor driver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driver.Id);
        if (driver.Models.Count == 0)
            throw new ArgumentException($"Driver '{driver.Id}' does not declare any radio models.", nameof(driver));

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RadioModelDescriptor model in driver.Models)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(model.Manufacturer);
            ArgumentException.ThrowIfNullOrWhiteSpace(model.Model);
            if (!ids.Add(model.Id))
                throw new ArgumentException($"Driver '{driver.Id}' declares model ID '{model.Id}' more than once.", nameof(driver));
            if (model.DefaultBaudRate is int baud && !model.SupportedBaudRates.Contains(baud))
                throw new ArgumentException($"Default baud rate {baud} is not supported by model '{model.Id}'.", nameof(driver));
        }
    }
}
