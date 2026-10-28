using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIHandler.Models.GuestSessions;

public record GetGroupSessionsResponse(
    [property: JsonPropertyName("participants")] IEnumerable<GroupSessionParticipant> Participants,
    [property: JsonPropertyName("total")] decimal Total);

public record GroupSessionParticipant(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")]
    [property: JsonConverter(typeof(JsonStringEnumConverter<ParticipantType>))] ParticipantType Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("items")] IEnumerable<GroupSessionItem> Items,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("isSubmitted")] bool IsSubmitted);

public record GroupSessionItem(
    [property: JsonPropertyName("dish")] GroupSessionDish Dish,
    [property: JsonPropertyName("quantity")] int Quantity);

public record GroupSessionDish(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("price")] decimal Price);
