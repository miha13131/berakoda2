using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace LocationsFeedback;

public class Function
{
    private readonly IAmazonDynamoDB _client;
    private readonly ObjectCache _cache; 

    public Function()
    {
        _client = new AmazonDynamoDBClient();
        _cache = MemoryCache.Default;
    }

    public Function(IAmazonDynamoDB client, ObjectCache cache)
    {
        _client = client;
        _cache = cache;
    }
    
    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var query = request.QueryStringParameters ?? new Dictionary<string, string>();
        
        if (request.PathParameters == null || !request.PathParameters.TryGetValue("id", out var filterLocationId))
            return ResponseCreator.CreateResponse(400, "Location ID is missing", null);
        
        string cacheKey = $"exists_loc_{filterLocationId}";
        
        if (!_cache.Contains(cacheKey)) 
        {
            var locationResponse = await _client.GetItemAsync(new GetItemRequest
            {
                TableName = "Locations",
                Key = new Dictionary<string, AttributeValue> { { "location_id", new AttributeValue { N = filterLocationId } } },
                ProjectionExpression = "location_id"
            });

            if (locationResponse.Item == null || locationResponse.Item.Count == 0)
            {
                _cache.Set(cacheKey, false, DateTimeOffset.UtcNow.AddMinutes(5));
                return ResponseCreator.CreateResponse(404, "Location not found", null);
            }
            
            _cache.Set(cacheKey, true, DateTimeOffset.UtcNow.AddMinutes(30));
        }
        else 
        {
            bool exists = (bool)_cache.Get(cacheKey);
            
            if (!exists) 
                return ResponseCreator.CreateResponse(404, "Location not found", null);
        }
        
        int page = query.TryGetValue("page", out var pStr) && int.TryParse(pStr, out var p) ? Math.Max(1, p) : 1;
        int size = query.TryGetValue("size", out var sStr) && int.TryParse(sStr, out var s) ? Math.Max(1, s) : 5;
        
        if (!query.TryGetValue("type", out var filterType) || string.IsNullOrEmpty(filterType))
            filterType = "SERVICE_QUALITY";

        if (filterType != "SERVICE_QUALITY" && filterType != "CUISINE_EXPERIENCE")
            return ResponseCreator.CreateResponse(400, "Invalid type. Allowed: SERVICE_QUALITY, CUISINE_EXPERIENCE", null);
        
        if (!query.TryGetValue("sort", out var sort) || string.IsNullOrEmpty(sort))
            sort = "date";

        if (sort != "rating" && sort != "date")
            return ResponseCreator.CreateResponse(400, "Invalid sort. Allowed: rating, date", null);
        
        var queryRequest = new QueryRequest
        {
            TableName = "feedbacks",
            IndexName = "location_id-index", 
            KeyConditionExpression = "location_id = :locationId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":locationId", new AttributeValue { N = filterLocationId } }
            }
        };
        
        var response = await _client.QueryAsync(queryRequest);
        
        context.Logger.LogLine($"Response items count: {response.Items.Count.ToString()}");

        if (response.Items == null || response.Items.Count == 0)
            return ResponseCreator.CreateResponse(404, "Feedbacks not found", new PageFeedbackResponse { items = new List<FeedbackDto>(), totalItems = 0 });

        var mapped = response.Items.Select(item => new FeedbackDto()
        {
            id = item.GetValueOrDefault("feedback_id")?.S,
            username = item.GetValueOrDefault("user_name")?.S ?? "Anonymous",
            date = item.GetValueOrDefault("date")?.S,
            description = item.GetValueOrDefault("description")?.S,
            rating = int.TryParse(item.GetValueOrDefault("rating")?.N, out var r) ? r : 0,
            type = item.GetValueOrDefault("type")?.S,
            locationId = int.Parse(filterLocationId),
            reservationId = item.GetValueOrDefault("reservation_id")?.S,
            image = item.GetValueOrDefault("user_avatar")?.S,
            userId = item.GetValueOrDefault("user_id")?.S,
        });
        
        mapped = filterType switch
        {
            "SERVICE_QUALITY" => mapped.Where(x => x.type == "SERVICE_QUALITY"),
            "CUISINE_EXPERIENCE" => mapped.Where(x => x.type == "CUISINE_EXPERIENCE"),
        };
        
        mapped = sort switch
        {
            "rating" => mapped.OrderByDescending(x => x.rating),
            "date" => mapped.OrderByDescending(x => x.date),
        };

        var allFilteredItems = mapped.ToList();
        var totalItems = allFilteredItems.Count;

        var pagedItems = allFilteredItems
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
