using System.Runtime.CompilerServices;

// The permission authorization bridge (PermissionRequirement, PermissionAuthorizationHandler), the JWT options
// and token provider, the API error codes, and the correlation-id feature are all internal, and must stay that
// way: none of them is part of the public HTTP contract. The API integration tests exercise the real bridge and
// issue real access tokens through them instead of re-implementing either, so they need friend access. Nothing
// is made public for testing.
[assembly: InternalsVisibleTo("SmartProperty.Api.IntegrationTests")]
