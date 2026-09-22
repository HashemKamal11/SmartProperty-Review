using System.Runtime.CompilerServices;

// The authentication use cases keep their Error catalogs and input validators internal: they are the use cases'
// own contract, not something another production assembly may depend on. The unit tests assert against those
// exact Error instances rather than re-declaring the codes as string literals, which would let a renamed or
// retyped error drift from its test unnoticed. Nothing is made public for testing.
[assembly: InternalsVisibleTo("SmartProperty.UnitTests")]
