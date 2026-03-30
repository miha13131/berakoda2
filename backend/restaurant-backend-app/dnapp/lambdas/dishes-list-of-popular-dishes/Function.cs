using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PopularDishes;

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
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue> lastKey = null;
        do {
            var resp = await _client.ScanAsync(new ScanRequest
            {
                TableName = "OrderItems",
                ExclusiveStartKey = lastKey
            });
            items.AddRange(resp.Items);
            lastKey = resp.LastEvaluatedKey?.Count > 0 ? resp.LastEvaluatedKey : null;
        } while (lastKey != null);
        
        var mapped = items
            .Select(item => new
            {
                DishId = item["dish_id"].N,
                Quantity = int.TryParse(item["quantity"].N, out var qty) ? qty : 0
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
            .ToList();
        
        var keys = result.Select(x => new Dictionary<string, AttributeValue>
        {
            { "dish_id", new AttributeValue { N = x.DishId } }
        }).ToList();

        var batchRequest = new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes> {{ "Dishes", new KeysAndAttributes { Keys = keys }}}
        };
        
        var allDishes = new List<Dictionary<string, AttributeValue>>();
        var unprocessed = batchRequest.RequestItems;
        do {
            var resp = await _client.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = unprocessed });
            allDishes.AddRange(resp.Responses["Dishes"]);
            unprocessed = resp.UnprocessedKeys?.Count > 0 ? resp.UnprocessedKeys : null;
        } while (unprocessed != null);

        var dishes = allDishes;
        
        var dishesDict = dishes.ToDictionary(d => d["dish_id"].N);

        var popularDishes = result
            .Select(r =>
            {
                if (!dishesDict.TryGetValue(r.DishId, out var d))
                    return null; // dish removed or damaged — skip

                return new DishDto
                {
                    id = int.TryParse(d["dish_id"].N, out var dId) ? dId : 0,
                    name = d["name"].S,
                    price = int.TryParse(d["price"].N, out var dPr) ? dPr : 0,
                    weight = int.TryParse(d["weight"].N, out var dWt) ? dWt : 0,
                    image = d["image_url"].S,
                    calories = int.TryParse(d["calories"].N, out var dCal) ? dCal : 0,
                    carbohydrates = d["carbohydrates"].S,
                    description = d["description"].S,
                    fats = d["fats"].S,
                    protein = d["protein"].S,
                    vitaminsAndMinerals = d["vitamins_and_minerals"].S,
                };
            })
            .Where(x => x != null)
            .ToList();
        
        return ResponseCreator.CreateResponse(200, "Successful return of the list of dishes!", popularDishes);
    }
}
