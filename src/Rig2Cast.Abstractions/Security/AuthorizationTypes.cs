namespace Rig2Cast.Abstractions.Security;

public enum ClientRole
{
    Observer,
    Operator,
    Controller,
    Administrator
}

public sealed record ClientIdentity(string Id, string? DisplayName = null);

public sealed record ClientAuthorization(
    long Revision,
    ClientIdentity Client,
    IReadOnlySet<ClientRole> Roles,
    bool CanObserve,
    bool CanControl,
    bool CanManageLeases);
