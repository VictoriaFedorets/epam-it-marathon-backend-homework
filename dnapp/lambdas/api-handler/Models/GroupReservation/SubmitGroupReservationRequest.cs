using System.Text.Json.Serialization;

namespace APIHandler.Models.GroupReservation;

public class SubmitGroupReservationRequest
{
    [JsonPropertyName("reservationId")]
    public string? ReservationId { get; set; }
}
