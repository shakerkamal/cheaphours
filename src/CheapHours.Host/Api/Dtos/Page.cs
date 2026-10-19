namespace CheapHours.Host.Api.Dtos;

/// <summary>One page of a list. Pass <see cref="NextCursor"/> back as <c>cursor</c> to get the next page.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Data">The items on this page.</param>
/// <param name="NextCursor">The cursor for the next page, or <see langword="null"/> on the last page.</param>
internal sealed record Page<T>(IReadOnlyList<T> Data, string? NextCursor);
