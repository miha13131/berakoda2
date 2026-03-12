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
            "/tables/locations/select-options" => HandleSelectOptions(response),
            "/tables/locations" => HandleFullList(response),
            _ => ResponseCreator.CreateResponse(404, "Not Found", null)
        };
    }

    public APIGatewayProxyResponse HandleFullList(ScanResponse response)
    {
        var locations = response.Items.Select(item => new LocationDto
        {
            id = item["location_id"].N.ToString(),
            address = $"{item["location_number"].N}, {item["location_name"].S}",
            totalCapacity = item["total_capacity"].N,
            averageOccupancy =  item["average_occupancy"].N,
            imageUrl =  item["image_url"].S,
            description = item["description"].N,
            rating = item["rating"].N
        }).ToList();
        
        return ResponseCreator.CreateResponse(200, "Successful return of the list of locations!", new { locationsList = locations });
    }

    public APIGatewayProxyResponse HandleSelectOptions(ScanResponse response)
    {
        var locations = response.Items.Select(item => new BriefLocationDto
        {
            id = item["location_id"].N.ToString(),
            value = $"{item["location_number"].N}, {item["location_name"].S}"
        }).ToList();
        
        return ResponseCreator.CreateResponse(200, "Successful return of the list of locations!", new { locationsList = locations });
    }
}
