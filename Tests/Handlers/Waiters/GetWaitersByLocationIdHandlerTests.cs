using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Handlers.Waiters;
using APIHandler.Models.WaiterList;
using APIHandler.Services.Waiters;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.Waiters;

public class GetWaitersByLocationIdHandlerTests
{
    private readonly Mock<IWaiterService> _mockWaiterService = new();
    private readonly GetWaitersByLocationIdHandler _handler;

    public GetWaitersByLocationIdHandlerTests()
    {
        _handler = new GetWaitersByLocationIdHandler(_mockWaiterService.Object);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnWaitersWithOnlyUserIdAndLocationId_WhenLocationIdIsValid()
    {
        const string locationId = "location-123";
        _mockWaiterService
            .Setup(service => service.GetWaitersByLocationIdAsync(locationId))
            .ReturnsAsync(new List<WaiterListItem>
            {
                new()
                {
                    UserId = "user-1",
                    LocationId = locationId,
                    Reservations = ["reservation-1"]
                },
                new()
                {
                    UserId = "user-2",
                    LocationId = locationId
                }
            });

        var response = await _handler.HandleRequest(
            new APIGatewayProxyRequest
            {
                PathParameters = new Dictionary<string, string> { ["locationId"] = locationId }
            },
            context: null!);

        Assert.Equal(200, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);

        var waiters = body.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, waiters.Length);
        Assert.Equal("user-1", waiters[0].GetProperty("userId").GetString());
        Assert.Equal(locationId, waiters[0].GetProperty("locationId").GetString());
        Assert.Equal("user-2", waiters[1].GetProperty("userId").GetString());
        Assert.Equal(locationId, waiters[1].GetProperty("locationId").GetString());
        Assert.All(waiters, waiter =>
            Assert.Equal(
                new[] { "userId", "locationId" },
                waiter.EnumerateObject().Select(property => property.Name).ToArray()));

        _mockWaiterService.Verify(
            service => service.GetWaitersByLocationIdAsync(locationId),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnEmptyArray_WhenLocationHasNoWaiters()
    {
        const string locationId = "location-empty";
        _mockWaiterService
            .Setup(service => service.GetWaitersByLocationIdAsync(locationId))
            .ReturnsAsync(new List<WaiterListItem>());

        var response = await _handler.HandleRequest(
            new APIGatewayProxyRequest
            {
                PathParameters = new Dictionary<string, string> { ["locationId"] = locationId }
            },
            context: null!);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("[]", response.Body);
        _mockWaiterService.Verify(
            service => service.GetWaitersByLocationIdAsync(locationId),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_ShouldReturnBadRequestAndNotQueryService_WhenLocationIdIsAbsent()
    {
        var response = await _handler.HandleRequest(
            new APIGatewayProxyRequest { PathParameters = new Dictionary<string, string>() },
            CreateContext());

        Assert.Equal(400, response.StatusCode);
        _mockWaiterService.Verify(
            service => service.GetWaitersByLocationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task HandleRequest_ShouldReturnBadRequestAndNotQueryService_WhenLocationIdIsBlank(string locationId)
    {
        var response = await _handler.HandleRequest(
            new APIGatewayProxyRequest
            {
                PathParameters = new Dictionary<string, string> { ["locationId"] = locationId }
            },
            CreateContext());

        Assert.Equal(400, response.StatusCode);
        _mockWaiterService.Verify(
            service => service.GetWaitersByLocationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    private static ILambdaContext CreateContext()
    {
        var logger = new Mock<ILambdaLogger>();
        var context = new Mock<ILambdaContext>();
        context.SetupGet(lambdaContext => lambdaContext.Logger).Returns(logger.Object);
        return context.Object;
    }
}
