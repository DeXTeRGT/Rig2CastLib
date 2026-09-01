 ## Critical findings

  1. Solicited responses and unsolicited announcements are ambiguously correlated.

     Both current ASCII protocol engines treat the first frame matching the expected prefix as the query response. With automatic-information mode enabled, an unsolicited FA, IF, MD, or similar announcement can incorrectly complete an
     in-flight query for the same command.

     This affects the existing Yaesu and Elecraft implementations today, independently of future protocols.
      - src/Rig2Cast.Drivers.Yaesu/Protocol/YaesuAsciiProtocol.cs:241
      - src/Rig2Cast.Drivers.Elecraft/Protocol/ElecraftAsciiProtocol.cs:204

     The tests cover an unsolicited frame with a different prefix, but not one sharing the pending query’s prefix:
      - tests/Rig2Cast.Runtime.Tests/YaesuAsciiProtocolTests.cs:53
      - tests/Rig2Cast.Runtime.Tests/ElecraftK3DriverTests.cs:131

  2. Cancellation can interrupt a frame write without invalidating the protocol session.

     Caller cancellation is passed directly to transport writes. If cancellation occurs after a partial write, the protocol simply propagates OperationCanceledException and allows later commands to use the same connection. That can leave
     the radio parser mid-command.

     This is already relevant to semicolon-delimited Yaesu and Elecraft CAT, not just future binary protocols.
      - src/Rig2Cast.Drivers.Yaesu/Protocol/YaesuAsciiProtocol.cs:222
      - src/Rig2Cast.Drivers.Elecraft/Protocol/ElecraftAsciiProtocol.cs:186

     Cancellation should remove work before transmission starts. Once a frame starts, either finish the write without caller cancellation or fault the connection if completion cannot be guaranteed.

  ## High findings

  3. Unsolicited-state loss is silent.

     Both protocols use bounded channels configured with DropOldest. Dropped announcements produce no gap notification and do not invalidate or refresh cached state. Under sustained dial or control activity, consumers can unknowingly
     receive stale state.
      - src/Rig2Cast.Drivers.Yaesu/Protocol/YaesuAsciiProtocol.cs:18
      - src/Rig2Cast.Drivers.Yaesu/Protocol/YaesuAsciiProtocol.cs:259
      - src/Rig2Cast.Drivers.Elecraft/Protocol/ElecraftAsciiProtocol.cs:18
      - src/Rig2Cast.Drivers.Elecraft/Protocol/ElecraftAsciiProtocol.cs:230

     The public event hub already has delivery-gap semantics, but the driver observation path does not provide an equivalent mechanism.

  4. Older queued observations can overwrite newer polled state.

     Observations are timestamped in the driver, queued through the scheduler, and applied without comparing their timestamp to the cached field’s timestamp. A delayed announcement can therefore overwrite a later ReadStateAsync result,
     and RadioState.ObservedAt can move backwards.
      - src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:746
      - src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:929
      - src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:994

  5. Receiver identity and VFO identity are conflated.

     VfoId contains both A/B and Main/Sub, and RadioState keys frequencies only by that enum. This is sufficient for the present FTDX10 and basic K3 usage, but it is not ready for future dual-receiver models where Main/Sub receivers
     independently select VFOs A/B.
      - src/Rig2Cast.Abstractions/Radios/RadioTypes.cs:3
      - src/Rig2Cast.Abstractions/Radios/RadioSnapshots.cs:6
      - src/Rig2Cast.Abstractions/Drivers/DriverContracts.cs:42

     This is an architectural blocker for broad model coverage, though not specifically a CI-V or binary-framing problem.

  6. Profiles are only partially declarative.

     The model profile types carry basic identity, modes, and a few model switches. Most protocol commands, response layouts, frequency limits, controls, meters, capabilities, and option interpretation remain hard-coded inside the
     drivers.
      - src/Rig2Cast.Drivers.Yaesu/Ftdx10/Ftdx10CatProfile.cs:5
      - src/Rig2Cast.Drivers.Yaesu/Ftdx10/Ftdx10Driver.cs:36
      - src/Rig2Cast.Drivers.Yaesu/Ftdx10/Ftdx10Driver.cs:646
      - src/Rig2Cast.Drivers.Elecraft/K3Family/ElecraftK3Profile.cs:5
      - src/Rig2Cast.Drivers.Elecraft/K3Family/ElecraftK3Driver.cs:721

     Adding closely related models will still require substantial branching inside driver code. This does not prevent new protocol families, but it undermines the stated profile-driven approach.

  ## Medium findings

  7. Published frequency capabilities contradict setter behavior.

     Both drivers publish their frequency range with Transmit: false, while separately advertising transmit support and permitting frequency writes within duplicated hard-coded limits.
      - src/Rig2Cast.Drivers.Yaesu/Ftdx10/Ftdx10Driver.cs:192
      - src/Rig2Cast.Drivers.Yaesu/Ftdx10/Ftdx10Driver.cs:659
      - src/Rig2Cast.Drivers.Elecraft/K3Family/ElecraftK3Driver.cs:107
      - src/Rig2Cast.Drivers.Elecraft/K3Family/ElecraftK3Driver.cs:738

     At minimum, the meaning of Transmit: false needs clarification. Ideally, receive and transmit ranges should be profile data and setters should validate against the same source.

  8. The requested layered test organization is not present.

     Existing coverage is substantial but concentrated in one xUnit project with inline scripted responses. There are no separate golden fixture assets, reusable cross-driver conformance suite, distinct end-to-end suite, or opt-in
     hardware test project/traits.
      - tests/Rig2Cast.Runtime.Tests/Rig2Cast.Runtime.Tests.csproj:1
      - tests/Rig2Cast.Runtime.Tests/YaesuAsciiProtocolTests.cs:95

     This finding concerns test structure and extensibility, not the absence of future-protocol tests.

  ## Architecture-readiness conclusion

  The byte-oriented IRadioTransport and optional IRadioObservationSource are good foundational decisions. A future driver can implement a different binary framing engine without changing IRadioTransport or the main IRadioDriver contract.
  RawFrame could also temporarily carry a hex representation, so its string type is not by itself a blocker.

  However, the architecture is not fully ready for broad coverage because observation loss/order semantics and the receiver/VFO model require core-level decisions. The duplicated ASCII protocol engines are primarily a maintainability
  concern: they do not prevent a future protocol family from being implemented correctly, but a shared internal protocol-pump abstraction would avoid reproducing transaction, shutdown, backpressure, and cancellation defects in each
  family.

  No finding is raised merely because CI-V or legacy Yaesu binary CAT has not yet been implemented.