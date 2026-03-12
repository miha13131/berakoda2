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
        
        var mapped = response.Items
            .Select(item => new
            {
                DishId = item["dish_id"].N,
                Quantity = int.Parse(item["quantity"].N)
            })
            .ToList();

        var grouped = mapped
            .GroupBy(x => x.DishId)
            .ToList();

        var aggregated = grouped
            .Select(group => new
            {
                DishId = group.Key,
                TotalOrdered = group.Sum(x => x.Quantity)
            })
            .ToList();

        var result = aggregated
            .OrderByDescending(x => x.TotalOrdered)
            .Take(6)
            .ToList();
        
        var keys = result.Select(x => new Dictionary<string, AttributeValue>
        {
            { "dish_id", new AttributeValue { N = x.DishId } }
        }).ToList();

        var batchRequest = new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes> {{ "Dishes", new KeysAndAttributes { Keys = keys }}}
        };

        var dishesResponse = await _client.BatchGetItemAsync(batchRequest);
        
        var dishes = dishesResponse.Responses["Dishes"];

        var orderItems = dishes.Select(d => new DishDto
        {
            id = d["dish_id"].N,
            name = d["name"].S,
            price = d["price"].N,
            weight = d["weight"].N,
            image = d["image_url"].S,
            calories = d["calories"].N,
            carbohydrates = d["carbohydrates"].S,
            description = d["description"].S,
            fats = d["fats"].S,
            protein = d["protein"].S,
            vitaminsAndMinerals = d["vitamins_and_minerals"].S,
        }).ToList();
        
        return ResponseCreator.CreateResponse(200, "Successful return of the list of locations!", new { popularDishes = orderItems });
    }
}
