using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Helpers;
using APIHandler.Services.Waiters;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace APIHandler.Handlers.Waiters;

public class GetWaitersByLocationIdHandler : IApiRequestHandler
{
    private readonly IWaiterService _waiterService;

    public GetWaitersByLocationIdHandler(IWaiterService waiterService)
    {
        _waiterService = waiterService ?? throw new ArgumentNullException(nameof(waiterService));
    }

    public async Task<APIGatewayProxyResponse> HandleRequest(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (request.PathParameters == null ||
            !request.PathParameters.TryGetValue("locationId", out var locationId) ||
            string.IsNullOrWhiteSpace(locationId))
        {
            return ResponseBuilder.CreateErrorResponse(400, "Missing or invalid locationId in path parameters", context.Logger);
        }

        var waiters = await _waiterService.GetWaitersByLocationIdAsync(locationId);
        var response = waiters
            .Select(waiter => new { userId = waiter.UserId, locationId = waiter.LocationId })
            .ToList();

        return ResponseBuilder.CreateSuccessResponse(response);
    }
}
