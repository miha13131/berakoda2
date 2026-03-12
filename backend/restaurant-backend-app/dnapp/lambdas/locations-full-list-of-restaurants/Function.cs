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

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var response = await _client.ScanAsync(new ScanRequest { TableName = "Locations" });

        return request.Path.TrimEnd('/') switch
        {
            "/locations/select-options" => HandleSelectOptions(response),
            "/locations" => HandleFullList(response),
            _ => ResponseCreator.CreateResponse(404, "Not Found", null)
        };
    }

    public APIGatewayProxyResponse HandleFullList(ScanResponse response)
    {
        var locations = response.Items.Select(item => new LocationDto
        {
            id = int.TryParse(item["location_id"].N, out var lId) ? lId : 0,
            address = item["address"].S,
            averageOccupancy = int.TryParse(item["average_occupancy"].N, out var a) ? a : 0,
            totalCapacity = int.TryParse(item["total_capacity"].N, out var t) ? t : 0, 
            image =  item["image_url"].S,
            description = item["description"].S,
            rating = double.TryParse(item["rating"].N, out var r) ? r : 0,
        }).ToList();
        
        return ResponseCreator.CreateResponse(200, "Successful return of the list of locations!", new { locationsList = locations });
    }

    public APIGatewayProxyResponse HandleSelectOptions(ScanResponse response)
    {
        var locations = response.Items.Select(item => new BriefLocationDto
        {
            id = int.TryParse(item["location_id"].N, out var lId) ? lId : 0,
            value = item["address"].S,
        }).ToList();
        
        return ResponseCreator.CreateResponse(200, "Successful return of the list of locations!", new { locationsList = locations });
    }
}
