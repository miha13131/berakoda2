using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace LeaveFeedback;

public class Feedback
{
    private readonly IAmazonDynamoDB _client;

    public Feedback()
    {
        _client = new AmazonDynamoDBClient();
    }
    
    public async Task<APIGatewayProxyResponse> FeedbackHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var httpMethod = request.HttpMethod;
        var path = request.Path.TrimEnd('/');
        var query = request.QueryStringParameters ?? new Dictionary<string, string>();
        
        query.TryGetValue("date", out var d);
        string? date = !string.IsNullOrEmpty(d) ? Uri.UnescapeDataString(d) : null;
        
        query.TryGetValue("slotId", out var s);
        string? slotId = !string.IsNullOrEmpty(s) ? Uri.UnescapeDataString(s) : null;
        
        query.TryGetValue("tableId", out var t);
        string? tableId = !string.IsNullOrEmpty(t) ? Uri.UnescapeDataString(t) : null;
        
        bool isReservationIdInvalid =
            string.IsNullOrEmpty(date) || string.IsNullOrEmpty(slotId) || string.IsNullOrEmpty(tableId);
        
        var reservationId = $"{date}#{slotId}#{tableId}";
        
        query.TryGetValue("feedback_id", out var encFeedbackId);
        string? feedbackId = !string.IsNullOrEmpty(encFeedbackId) ? Uri.UnescapeDataString(encFeedbackId) : null;
        
        try
        {
            if (httpMethod == "GET" && path == "/feedback/get-waiter")
            {
                if (isReservationIdInvalid)
                    return ResponseCreator.CreateResponse(400, "Invalid request", $"Reservation info `{date}#{slotId}#{tableId}` is invalid. Expected format: `2026-03-30#5#1`");
                
                return await GetWaiterInfo(reservationId);
            }
            
            if (httpMethod == "POST" && path.EndsWith("/leave-feedback")) 
            {
                if (isReservationIdInvalid)
                    return ResponseCreator.CreateResponse(400, "Invalid request", $"Reservation info `{date}#{slotId}#{tableId}` is invalid. Expected format: `2026-03-30#5#1`");
                
                var segments = path.Trim('/').Split('/');
                int locationId = (segments.Length == 3 && int.TryParse(segments[1], out var lId)) ? lId : 0;
                return await LeaveFeedback(request, query, locationId, reservationId, context);
            }

            if (httpMethod == "GET" && path.EndsWith("/get-feedback"))
                return await GetFeedback(feedbackId);
            
            if (httpMethod == "PUT" && path == "/feedback/update-feedback")
                return await UpdateFeedback(request, query, feedbackId, context);

            return ResponseCreator.CreateResponse(404, "Not Found", $"Route {httpMethod} {path} not mapped.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", "Something went wrong.");
        }
    }

    private async Task<APIGatewayProxyResponse> GetWaiterInfo(string reservationId)
    {
        var queryRequest = new QueryRequest
        {
            TableName = "Reservations",
            IndexName = "reservation_id-index",
            KeyConditionExpression = "reservation_id_sk = :reservation_id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":reservation_id", new AttributeValue { S = reservationId } },
            }
        };
        
        var response = await _client.QueryAsync(queryRequest);
        
        if (response.Items == null || response.Items.Count == 0)
            return ResponseCreator.CreateResponse(404, "Not Found", "Reservation not found.");
        
        var mapped = response.Items
            .Select(item => new
            {
                email = item.ContainsKey("waiter_id") && !string.IsNullOrEmpty(item["waiter_id"].S)
                    ? item["waiter_id"].S
                    : null
            })
            .ToList();
        
        var validEmails = mapped
            .Where(x => !string.IsNullOrEmpty(x.email))
            .Select(x => x.email)
            .Distinct()
            .ToList();
        
        if (!validEmails.Any())
            return ResponseCreator.CreateResponse(400, "No waiters assigned", null);
        
        var keys= validEmails.Select(email => new Dictionary<string, AttributeValue>
        {
            { "email", new AttributeValue { S = email } }
        }).ToList();
        
        var batchRequest = new BatchGetItemRequest { 
            RequestItems = new Dictionary<string, KeysAndAttributes>
            {
                { "waiters-list", new KeysAndAttributes { Keys = keys } }
            }
        };
        
        var waiterResponse = await _client.BatchGetItemAsync(batchRequest);
        
        var waiter = waiterResponse.Responses["waiters-list"];
        
        var result = waiter.Select(w => new
        {
            name = w["name"].S,
            photo = w["photo"].S,
            rating = double.TryParse(w["rating"].N, out double r) ? r : 0,
        }).ToList();
        
        if (waiter.Count == 0)
            return ResponseCreator.CreateResponse(200, "Some waiters were not found", new
            {
                data = result,
                warning = "Waiter ID exist but no waiters found in database."
            });
        
        return ResponseCreator.CreateResponse(200, "Successful return of the information about waiter!", result);
    }

    private async Task<APIGatewayProxyResponse> LeaveFeedback(APIGatewayProxyRequest request, IDictionary<string, string> query, int locationId, string? reservationId, ILambdaContext context)
    {
        var reservationResponse = await _client.QueryAsync(new QueryRequest
        {
            TableName = "Reservations",
            IndexName = "reservation_id-index",
            KeyConditionExpression = "reservation_id_sk = :rid",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":rid", new AttributeValue { S = reservationId } }
            },
            ProjectionExpression = "reservation_id_sk"
        });
        
        if (reservationResponse.Items == null || reservationResponse.Items.Count == 0)
            return ResponseCreator.CreateResponse(404, "Reservation not found", null);
        
        query.TryGetValue("status", out var status);
        query.TryGetValue("type", out var feedbackType);
        
        FeedbackDto? feedback;
        
        if (locationId <= 0)
            return ResponseCreator.CreateResponse(400, "Invalid request", "Location ID is invalid.");

        if (query.Count == 0)
            return  ResponseCreator.CreateResponse(400, "Invalid request", "Query is empty.");
        
        if (status != "InProgress")
            return ResponseCreator.CreateResponse(400, "Invalid request", "Status is invalid.");
        
        if (feedbackType != "SERVICE_QUALITY" && feedbackType != "CUISINE_EXPERIENCE")
            return ResponseCreator.CreateResponse(400, "Invalid request", "Type is invalid.");
        
        var location = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = "Locations",
            Key = new Dictionary<string, AttributeValue> { { "location_id", new AttributeValue { N = locationId.ToString() } } },
            ProjectionExpression = "location_id"
        });

        if (location.Item == null || location.Item.Count == 0)
            return ResponseCreator.CreateResponse(404, "Not Found", "Location not found.");
        
        if (string.IsNullOrWhiteSpace(request.Body))
            return ResponseCreator.CreateResponse(400, "Invalid request", "Request body is empty.");
        
        try
        {
            feedback = JsonSerializer.Deserialize<FeedbackDto>(request.Body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });
        }
        catch (JsonException ex)
        {
            return ResponseCreator.CreateResponse(400, "Invalid request", $"Invalid payload: {ex.Message}");
        }
        
        if (feedback == null)
            return ResponseCreator.CreateResponse(400, "Invalid request", "Request body is null.");
        
        if (feedback.description == null)
            feedback.description = "";
        
        if (feedback.rating < 1 || feedback.rating > 5)
            return ResponseCreator.CreateResponse(400, "Invalid request", "Rating must be greater than 0 and less or equal to 5.");
        
        var claims = request.RequestContext?.Authorizer?.Claims;

        string userId;
        string email;
        string firstName;
        string lastName;
        string userAvatar;

        if (claims != null)
        {
            userId = claims.ContainsKey("sub") ? claims["sub"] : "test-user-id";
            email = claims.ContainsKey("email") ? claims["email"] : "test@email.com";
            firstName = claims.ContainsKey("given_name") ? claims["given_name"] : "Test";
            lastName = claims.ContainsKey("family_name") ? claims["family_name"] : "User";
            userAvatar = claims.ContainsKey("picture") ? claims["picture"] : "https://thumbs.dreamstime.com/b/default-avatar-profile-icon-social-media-user-vector-image-icon-default-avatar-profile-icon-social-media-user-vector-image-209162840.jpg";
        }
        else
        {
            userId = "test-user-id";
            email = "test@email.com";
            firstName = "Test";
            lastName = "User";
            userAvatar = "https://thumbs.dreamstime.com/b/default-avatar-profile-icon-social-media-user-vector-image-icon-default-avatar-profile-icon-social-media-user-vector-image-209162840.jpg";
        }

        var userName = $"{firstName} {lastName}".Trim();

        if (string.IsNullOrWhiteSpace(userName))
            userName = email;

        var item = new Dictionary<string, AttributeValue>
        {
            { "feedback_id", new AttributeValue { S = Guid.NewGuid().ToString() } },
            { "location_id", new AttributeValue { N = locationId.ToString() } },
            { "reservation_id", new AttributeValue { S = reservationId } },
            { "description", new AttributeValue { S = feedback.description } },
            { "rating", new AttributeValue { N = feedback.rating.ToString() } },
            { "date", new AttributeValue { S = DateTime.UtcNow.ToString() } },
            { "type", new AttributeValue { S = feedbackType } },
            { "user_id", new AttributeValue { S = userId } },
            { "user_name", new AttributeValue { S = userName } },
            { "user_avatar", new AttributeValue { S = userAvatar ?? "" } },
        };
        
        await _client.PutItemAsync(new PutItemRequest { TableName = "feedbacks", Item = item });
        
        return ResponseCreator.CreateResponse(200, "Feedback saved successfully!", new { description = feedback.description, rating = feedback.rating, });
    }

    private async Task<APIGatewayProxyResponse> UpdateFeedback(APIGatewayProxyRequest request, IDictionary<string, string> query, string? feedbackId, ILambdaContext context)
    {
        if (string.IsNullOrEmpty(feedbackId))
            return ResponseCreator.CreateResponse(400, "Invalid request", "Feedback ID is invalid.");
        
        query.TryGetValue("status", out var status);
        
        context.Logger.LogLine(status);
        
        if (status != "Finished")
            return ResponseCreator.CreateResponse(400, "Invalid request", "Status is invalid.");
        
        FeedbackDto? updatedFeedback = null;

        try
        {
            updatedFeedback = JsonSerializer.Deserialize<FeedbackDto>(request.Body ?? "", new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });

        }
        catch (JsonException ex)
        {
            return ResponseCreator.CreateResponse(400, "Invalid request", $"Invalid payload: {ex.Message}");
        }
        
        if (updatedFeedback == null)
            return ResponseCreator.CreateResponse(400, "Invalid request", "Request body is empty.");
        
        if (updatedFeedback.rating < 1 || updatedFeedback.rating > 5)
            return ResponseCreator.CreateResponse(400, "Invalid request", "Rating must be greater than 0 and less or equal to 5.");
        
        var queryResponse = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = "feedbacks",
            Key = new Dictionary<string, AttributeValue> { { "feedback_id", new AttributeValue { S = feedbackId } } },
            ProjectionExpression = "feedback_id"
        });
        
        if (queryResponse.Item == null || queryResponse.Item.Count == 0)
            return ResponseCreator.CreateResponse(404, "Not Found", "Feedback does not exist.");
        
        var updateRequest = new UpdateItemRequest
        {
            TableName = "feedbacks",
            Key = new Dictionary<string, AttributeValue>
            {
                { "feedback_id", new AttributeValue { S = feedbackId } } 
            },
            UpdateExpression = "SET description = :desc, rating = :rat, #date = :date",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#date", "date" } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { 
                { ":desc", new AttributeValue { S = updatedFeedback.description ?? "" } }, 
                { ":rat", new AttributeValue { N = updatedFeedback.rating.ToString() } },
                { ":date", new AttributeValue { S = DateTime.UtcNow.ToString() } },
            }
        };

        await _client.UpdateItemAsync(updateRequest);
            
        return ResponseCreator.CreateResponse(200, "Feedback updated successfully!", null);
    }

    private async Task<APIGatewayProxyResponse> GetFeedback(string? feedbackId)
    {
        if (string.IsNullOrEmpty(feedbackId))
            return ResponseCreator.CreateResponse(400, "Invalid request", "Feedback ID is invalid.");
        
        var queryRequest = new QueryRequest
        {
            TableName = "feedbacks",
            KeyConditionExpression = "feedback_id = :feedbackId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":feedbackId", new AttributeValue { S = feedbackId } },
            }
        };
            
        var queryResponse = await _client.QueryAsync(queryRequest);
        
        if (queryResponse.Items == null || queryResponse.Items.Count == 0)
            return ResponseCreator.CreateResponse(404, "Not Found", "Feedback does not exist.");
        
        var mapped = queryResponse.Items
            .Select(item => new FeedbackDto()
            {
                id = item["feedback_id"].S,
                locationId = int.TryParse(item["location_id"].N, out var lId) ? lId : 0,
                reservationId =  item["reservation_id"].S,
                description = item["description"].S,
                rating = int.TryParse(item["rating"].N, out var r) ? r : 0,
                date = item["date"].S,
                type = item["type"].S,
                image = item["user_avatar"].S, 
                userId = item["user_id"].S,
                username = item["user_name"].S,
            })
            .ToList();
        
        return ResponseCreator.CreateResponse(200, "Feedback returned successfully!", mapped);
    }
}