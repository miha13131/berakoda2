using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SimpleLambdaFunction;

public class Function
{
    private readonly IAmazonDynamoDB _client;

    public Function()
    {
        _client = new AmazonDynamoDBClient();
    } 
    
    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var response = await _client.ScanAsync(new ScanRequest { TableName = "OrderItems" });
        
        string locationId = request.PathParameters != null &&
                            request.PathParameters.ContainsKey("id")
            ? request.PathParameters["id"]
            : null;

        if (locationId == null)
            return ResponseCreator.CreateResponse(400, "Location ID is missing", null);
        
        var mapped = response.Items
            .Select(item => new
            {
                DishId = item["dish_id"].N,
                Quantity = int.Parse(item["quantity"].N),
                LocationId = item["location_id"].N
            })
            .ToList();
        
        var filtered = mapped
            .Where(x => x.LocationId == locationId)
            .ToList();
        
        var aggregated = filtered
            .GroupBy(x => x.DishId)
            .Select(group => new
            {
                DishId = group.Key,
                TotalOrdered = group.Sum(x => x.Quantity)
            })
            .ToList();
        
        var top = aggregated
            .OrderByDescending(x => x.TotalOrdered)
            .Take(6)
            .ToList();
        
        if (!top.Any())
        {
            return ResponseCreator.CreateResponse(200, "No orders found for this location", new List<DishDto>());
        }

        var keys = top.Select(x => new Dictionary<string, AttributeValue>
        {
            { "dish_id", new AttributeValue { N = x.DishId } }
        }).ToList();

        var batchRequest = new BatchGetItemRequest
        { RequestItems = new Dictionary<string, KeysAndAttributes> {{ "Dishes", new KeysAndAttributes { Keys = keys }
                }
            }
        };

        var dishesResponse = await _client.BatchGetItemAsync(batchRequest);

        var dishes = dishesResponse.Responses["Dishes"];

        var result = dishes.Select(d => new DishDto
        {
            id = d["dish_id"].N,
            name = d["name"].S,
            price = d["price"].N,
            weight = d["weight"].N,
            image = d["image_url"].S
        }).ToList();
        
        return ResponseCreator.CreateResponse(200, "Successful return of the specific restaurant dishes!", new { popularDishes = result });
    }
}
