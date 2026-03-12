using System;
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
        var response = await _client.ScanAsync(new ScanRequest { TableName = "Feedbacks" });
        var query = request.QueryStringParameters;

        string filterLocationId = request.PathParameters != null &&
                            request.PathParameters.ContainsKey("id")
            ? request.PathParameters["id"]
            : null;
        
        if (filterLocationId == null)
            return ResponseCreator.CreateResponse(400, "Location ID is missing", null);
        
        string sort = null;
        string filterType = null;
        int page = 1;
        int size = 5;

        if (query != null)
        {
            query.TryGetValue("sort", out sort);
            query.TryGetValue("type", out filterType);

            if (query.TryGetValue("page", out var p))
                page = int.Parse(p);

            if (query.TryGetValue("size", out var s))
                size = int.Parse(s);
        }
        
        var mapped = response.Items.Select(item => new FeedbackDto()
        {
            id = item["feedback_id"].N.ToString(),
            username = item["username"].S,
            date =  item["date"].S,
            imageUrl =  item["image_url"].S,
            description = item["description"].S,
            rating = item["rating"].N,
            type = item["type"].S,
            locationId = item["location_id"].N
        }).ToList();
        
        var filtered = mapped
            .Where(x => x.locationId == filterLocationId)
            .ToList();
        
        if (!string.IsNullOrEmpty(filterType))
        {
            filtered = filtered
                .Where(x => x.type == filterType)
                .ToList();
        }
        
        if (sort == "date")
        {
            filtered = filtered
                .OrderByDescending(x => x.date)
                .ToList();
        }
        else if (sort == "rating")
        {
            filtered = filtered
                .OrderByDescending(x => x.rating)
                .ToList();
        }
        
        var totalItems = filtered.Count;

        var pagedItems = filtered
            .Skip((page - 1) * size)
            .Take(size)
            .ToList();
        
        var totalPages = (int)Math.Ceiling((double)totalItems / size);
        
        var result = new PageFeedbackResponse
        {
            items = pagedItems,
            page = page,
            size = size,
            totalItems = totalItems,
            totalPages = totalPages
        };
        
        return ResponseCreator.CreateResponse(200, "Feedbacks retrieved successfully", result);
    }
}
