# Mode-dependent control applicability

`ModeApplicabilityDescriptor` is optional capability metadata shared by numeric,
switch, choice, and meter descriptors. Its nullable `ReadModes`, `WriteModes`, and
`OperationalModes` sets distinguish protocol validity from application relevance.
A null set means unrestricted, preserving existing driver behavior.

Drivers own verified model facts. `ManagedRadioOptions.ModeApplicabilityPolicy`
selects whether the runtime enforces hard restrictions before sending CAT traffic or
treats them as advisory metadata. Enforcement remains the compatibility default.
Applications own presentation policy:
the capability GUI also treats operationally irrelevant controls as unavailable,
disables their editors, skips them during bulk refresh, and refreshes controls once
when a mode transition makes them newly readable.

Do not infer restrictions from a generic control name. Populate them from official
documentation or physical evidence because different manufacturers may accept the
same conceptual control in different modes. The initial physical pilot restricts the
FTDX10 audio peak filter switch, offset, and width to CW and CW-R, and marks
microphone gain unavailable in CW/CW-R. Physical testing subsequently found that
the radio may still answer these commands when the other VFO is in a compatible
mode, so applications can select advisory policy until multi-VFO applicability is
modelled. Other current descriptors remain unrestricted.

Runtime validation uses the managed session's current mode, including target- or
receiver-specific mode where those APIs are used. Direct driver consumers remain
responsible for respecting advertised metadata; `ManagedRadio` is the enforcement
boundary for normal applications when enforcement is selected.

Applications can reuse one options value as their global policy or override it for
an individual managed radio:

```csharp
var runtimeOptions = new ManagedRadioOptions
{
    ModeApplicabilityPolicy = ModeApplicabilityPolicy.Advisory
};

await using ManagedRadio radio = await ManagedRadio.CreateAsync(
    "radio-1", driver, runtimeOptions);
```

`Advisory` preserves all metadata returned in `RadioCapabilities` but does not block
descriptor-level mode applicability or per-choice applicable-mode checks. Direct
driver consumers remain responsible for interpreting the metadata themselves.
