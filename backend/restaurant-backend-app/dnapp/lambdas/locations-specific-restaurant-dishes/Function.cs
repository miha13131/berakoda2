using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace LocationsSpecific;

public class Function
{
    private readonly IAmazonDynamoDB _client;

    public Function()
    {
        _client = new AmazonDynamoDBClient();
    }

    public Function(IAmazonDynamoDB client)
    {
        _client = client;
    }
    
    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        string locationId = request.PathParameters != null &&
                            request.PathParameters.ContainsKey("id")
            ? request.PathParameters["id"]
            : null;

        if (locationId == null)
            return ResponseCreator.CreateResponse(400, "Location ID is missing", null);
        
        var location = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = "Locations",
            Key = new Dictionary<string, AttributeValue>
            {
                { "location_id", new AttributeValue { N = locationId } }
            }
        });

        if (location.Item == null || location.Item.Count == 0)
        {
            return ResponseCreator.CreateResponse(404, "Location not found", null);
        }
        
        var queryRequest = new QueryRequest
        {
            TableName = "OrderItems",
            IndexName = "OrderItemsLocation",
            KeyConditionExpression = "location_id = :locationId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":locationId", new AttributeValue { N = locationId } },
            }
        };
        
        var response = await _client.QueryAsync(queryRequest);
        
        var mapped = response.Items
            .Select(item => new
            {
                DishId = item["dish_id"].N,
                Quantity = int.TryParse(item["quantity"].N, out var qty) ? qty : 0,
                LocationId = item["location_id"].N
            })
            .ToList();
        
        /* // KeyConditionExpression = "location_id = :locationId", so filtering is not needed
        var filtered = mapped
            .Where(x => x.LocationId == locationId)
            .ToList();
        */ 
        
        var aggregated = mapped
            .GroupBy(x => x.DishId)
            .Select(group => new
            {
                DishId = group.Key,
                TotalOrdered = group.Sum(x => x.Quantity)
            })
            .ToList();
        
        var top = aggregated
            .OrderByDescending(x => x.TotalOrdered)
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
        { 
            RequestItems = new Dictionary<string, KeysAndAttributes> {{ "Dishes", new KeysAndAttributes { Keys = keys } } }
        };

        var allDishes = new List<Dictionary<string, AttributeValue>>();
        var unprocessed = batchRequest.RequestItems;
        do {
            var batchResp = await _client.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = unprocessed });
            allDishes.AddRange(batchResp.Responses["Dishes"]);
            unprocessed = batchResp.UnprocessedKeys?.Count > 0 ? batchResp.UnprocessedKeys : null;
        } while (unprocessed != null);

        var dishes = allDishes;

        var result = dishes.Select(d => new DishDto
        {
            id = int.TryParse(d["dish_id"].N, out var dId) ? dId : 0,
            name = d["name"].S,
            price = int.TryParse(d["price"].N, out var dPrice) ? dPrice : 0,
            weight = int.TryParse(d["weight"].N, out var dWeight) ? dWeight : 0,
            image = d["image_url"].S,
            calories = int.TryParse(d["calories"].N, out var dCalories) ? dCalories : 0,
            carbohydrates = d["carbohydrates"].S,
            description = d["description"].S,
            fats = d["fats"].S,
            protein = d["protein"].S,
            vitaminsAndMinerals = d["vitamins_and_minerals"].S,
        }).ToList();
        
        return ResponseCreator.CreateResponse(200, "Successful return of the specific restaurant dishes!", new { popularDishes = result });
    }
}
