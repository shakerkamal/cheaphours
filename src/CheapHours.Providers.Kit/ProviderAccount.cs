using CheapHours.Core.Ids;

namespace CheapHours.Providers.Kit;

/// <summary>
/// An owner's link to a vendor cloud. Tokens are not carried here: the kit's token store (built with the cloud providers) holds
/// them encrypted and attaches them to requests.
/// </summary>
/// <param name="ProviderId">The provider the account belongs to.</param>
/// <param name="OwnerId">The CheapHours owner who linked it.</param>
/// <param name="ExternalAccountId">The vendor's id for the account.</param>
public sealed record ProviderAccount(string ProviderId, OwnerId OwnerId, string ExternalAccountId);
