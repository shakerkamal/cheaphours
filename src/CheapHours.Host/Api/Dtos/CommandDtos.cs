using CheapHours.Core.Commands;
using CheapHours.Core.Ids;

namespace CheapHours.Host.Api.Dtos;

/// <summary>The body of <c>POST /v1/devices/{id}/commands</c>.</summary>
/// <param name="Type">What the device must do.</param>
/// <param name="Parameters">Command parameters; <c>maxPowerW</c> is required for <c>SetPowerLimit</c>.</param>
internal sealed record CreateCommandRequest(CommandType Type, CommandParametersDto? Parameters);

/// <summary>Command parameters.</summary>
/// <param name="MaxPowerW">Maximum power in W.</param>
internal sealed record CommandParametersDto(int? MaxPowerW);

/// <summary>A command accepted for asynchronous delivery.</summary>
/// <param name="Id">The command id; follow it at <c>/v1/commands/{id}</c>.</param>
/// <param name="State">The command's state, <c>Pending</c> when just created.</param>
internal sealed record CommandAcceptedResponse(CommandId Id, CommandState State);

/// <summary>A command and its history.</summary>
/// <param name="Id">The command id.</param>
/// <param name="DeviceId">The device that must act.</param>
/// <param name="Type">What the device must do.</param>
/// <param name="Parameters">The command's parameters, if any.</param>
/// <param name="State">The current state.</param>
/// <param name="Attempts">How many times it has been sent.</param>
/// <param name="History">Every state it has been in, oldest first.</param>
internal sealed record CommandDto(
    CommandId Id,
    DeviceId DeviceId,
    CommandType Type,
    CommandParametersDto? Parameters,
    CommandState State,
    int Attempts,
    IReadOnlyList<CommandHistoryEntryDto> History);

/// <summary>One step of a command's history.</summary>
/// <param name="State">The state entered.</param>
/// <param name="At">When (UTC).</param>
/// <param name="VendorRef">The vendor's reference, when the step involved the vendor.</param>
internal sealed record CommandHistoryEntryDto(CommandState State, DateTimeOffset At, string? VendorRef);
