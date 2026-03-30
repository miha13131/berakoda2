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

    public Function(IAmazonDynamoDB client)
    {
        _client = client;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var query = request.QueryStringParameters ?? new Dictionary<string, string>();
        
        if (!query.ContainsKey("type") || !query.ContainsKey("sort"))
        {
            return ResponseCreator.CreateResponse(400, "Query parameters 'type' and 'sort' are required.", null);
        }

        string type = query["type"];
        string sort = query["sort"];
        
        if (type != "APPETIZERS" && type != "MAIN_COURCES" && type != "DESSERTS")
            return ResponseCreator.CreateResponse(400, "The type field is invalid", null);

        if (sort != "ASC" && sort != "DESC" && sort != "LOW_TO_HIGH" && sort != "HIGH_TO_LOW")
            return ResponseCreator.CreateResponse(400, "The sorting field is invalid", null);
        
        string indexName = sort switch
        {
            "LOW_TO_HIGH" or "HIGH_TO_LOW" => "SortByPriceIndex",
            "ASC" or "DESC" => "SortByPopularityIndex",
            _ => "SortByPopularityIndex"
        };
        
        bool ascending = sort != "DESC" && sort != "HIGH_TO_LOW";

        var queryRequest = new QueryRequest
        {
            TableName = "Dishes",
            IndexName = indexName,
            KeyConditionExpression = "#type = :type",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#type", "type" } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":type", new AttributeValue { S = type } },
            },
            ScanIndexForward = ascending
        };
        
        var response = await _client.QueryAsync(queryRequest);

        var mapped = response.Items.Select(item => new
        {
            id = int.Parse(item["dish_id"].N),
            name = item["name"].S,
            price = int.Parse(item["price"].N),
            weight = int.Parse(item["weight"].N),
            image = item["image_url"].S,
            calories = int.Parse(item["calories"].N),
            carbohydrates = item["carbohydrates"].S,
            description = item["description"].S,
            fats = item["fats"].S,
            protein = item["protein"].S,
            vitaminsAndMinerals = item["vitamins_and_minerals"].S,
            type = item["type"].S,
            popularity = int.Parse(item["popularity"].N),
            isAvailable = item["is_available"].BOOL,
        }).ToList();;
        
        context.Logger.LogLine("mapped: " + mapped.Count);
        
        return ResponseCreator.CreateResponse(200, "Successful return of the dishes menu!", new { menu = mapped });
    }
}
