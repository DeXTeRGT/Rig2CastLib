# Driver development

A plugin exposes metadata through a sidecar manifest so it can be discovered without loading its assembly. Loading requires an explicit trust record containing at least plugin ID and SHA-256 binary hash; development mode may opt out.

Drivers compose transport, framing/codec, manufacturer or protocol-family behavior, and model-specific capability/quirk declarations. Declarative descriptions are encouraged for regular commands, while exceptional behavior remains expressible in C#.

The first physical target is Yaesu FTDX10. A deterministic simulator precedes hardware integration so scheduling, leases, parsing, timeouts, disconnections, and unsolicited events can be tested repeatably.
