using System.Text.Json.Nodes;

namespace CheapHours.Providers.Ocpp.Messages;

/// <summary>An OCPP call the gateway sends to a charger: the action and its payload.</summary>
/// <param name="Action">The action, for example <c>RemoteStartTransaction</c>.</param>
/// <param name="Payload">The request payload.</param>
public sealed record OcppCommandCall(string Action, JsonObject Payload);
