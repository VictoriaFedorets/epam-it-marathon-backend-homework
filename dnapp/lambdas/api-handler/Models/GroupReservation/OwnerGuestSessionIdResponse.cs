using System.Text.Json.Serialization;

namespace APIHandler.Models.GroupReservation;

public record OwnerGuestSessionIdResponse(
    [property: JsonPropertyName("guestSessionId")] string GuestSessionId);
