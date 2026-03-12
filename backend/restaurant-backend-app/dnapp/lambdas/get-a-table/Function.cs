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
        var queryParameters = request.QueryStringParameters ?? new Dictionary<string, string>();
        
        if (request.PathParameters == null || !request.PathParameters.TryGetValue("id", out var locationId))
            return ResponseCreator.CreateResponse(400, "Invalid request", "Location id is missing in path");
        
        queryParameters.TryGetValue("date", out var date);
        queryParameters.TryGetValue("timeStart", out var timeStart);
        queryParameters.TryGetValue("timeEnd", out var timeEnd);

        int guests = queryParameters.TryGetValue("guests", out var g) ? int.Parse(g) : 1;
        
        string startTimeForDb = "", endTimeForDb = "";
        
        date = date?.Trim('"');
        timeStart = timeStart?.Trim('"');
        timeEnd = timeEnd?.Trim('"');
        
        if (!string.IsNullOrEmpty(timeStart) && !string.IsNullOrEmpty(timeEnd))
        {
            string cleanStartTime = timeStart.Replace(".", "").ToUpper();
            string cleanEndTime = timeEnd.Replace(".", "").ToUpper();
            
            if (DateTime.TryParseExact(cleanStartTime, "h:mmtt", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime parsedStartTime) &&
                DateTime.TryParseExact(cleanEndTime, "h:mmtt", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime parsedEndTime))
            {
                startTimeForDb = parsedStartTime.ToString("HH:mm");
                endTimeForDb = parsedEndTime.ToString("HH:mm");
            }
        }
        
        var queryRequest = new QueryRequest
        {
            TableName = "Tables",
            KeyConditionExpression = "location_id = :locationId",
            FilterExpression = "#cap >= :guests",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#cap", "capacity" } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":locationId", new AttributeValue { N = locationId.ToString() } },
                { ":guests", new AttributeValue { N = guests.ToString() } }
            }
        };
        var response = await _client.QueryAsync(queryRequest);
        
        var mapped = response.Items.Select(item => new TableDto()
        {
            id = int.TryParse(item["table_id"].N, out var id) ? id : 0,
            capacity = int.TryParse(item["capacity"].N, out var capacity) ? capacity : 0,
            name = item.ContainsKey("name") ? item["name"].S : "Unknown",
            image = item.ContainsKey("image") ? item["image"].S : "",
            address = item.ContainsKey("address") ? item["address"].S : "",
        }).ToList();
        
        context.Logger.LogLine("mapped: " + mapped.Count);
        
        var tableSlotsResponse = await _client.ScanAsync(new ScanRequest { TableName = "Slots" });
        
        context.Logger.LogLine("tableSlotsResponse: " + tableSlotsResponse.Count);
        
        var slotMetadata = tableSlotsResponse.Items
            .Where(i => i.ContainsKey("slot_id"))
            .ToDictionary(
                i => i["slot_id"].N, 
                i => new 
                { 
                    Slot = i.ContainsKey("slot") ? i["slot"].S : "", 
                    Label = i.ContainsKey("label") ? i["label"].S : "" 
                }
            );
        
        context.Logger.LogLine("slotMetadata: " + slotMetadata.Count);
        
        var queryTasks = mapped.Select(async table =>
        {
            var slotsRequest = new QueryRequest
            {
                TableName = "Reservations",IndexName = "ReservationIndex",
                KeyConditionExpression = "table_id = :tid AND date_time_start BETWEEN :startTimeForDb AND :endOfDay",
                FilterExpression = "location_id = :locId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":tid", new AttributeValue { N = table.id.ToString() } },
                    { ":startTimeForDb", new AttributeValue { S = $"{date}#{startTimeForDb}" } },
                    { ":endOfDay", new AttributeValue { S = $"{date}#23:59" } },
                    { ":locId", new AttributeValue { N = locationId } }
                }
            };
            
            var res = await _client.QueryAsync(slotsRequest);
            var allItems = res.Items;
            
            var freeSlots = allItems
                .Where(s => s.ContainsKey("is_available") && s["is_available"].BOOL == true)
                .ToList();
            
            if (!freeSlots.Any())
                return null;

            return new 
            { 
                tableName = table.name,
                photo = table.image,
                isAvailableNow = true,
                slotsCount = freeSlots.Count,
                capacity = table.capacity,
                
                slots = freeSlots.Select(s => 
                {
                    string resSk = s.ContainsKey("reservation_id_sk") ? s["reservation_id_sk"].S : "";
                    
                    string currentSlotId = "";
                    var parts = resSk.Split('#');
                    
                    if (parts.Length >= 3) 
                    {
                        currentSlotId = parts[1];
                    }
                    
                    var meta = slotMetadata.ContainsKey(currentSlotId) ? slotMetadata[currentSlotId] : null;

                    return new 
                    {
                        id = resSk,
                        slot = meta != null ? meta.Slot : "Unknown",
                        label = meta != null ? meta.Label : "Unknown",
                        isAvailable = true 
                    };
                }).ToList()
            };
        });
        
        var finalResults = (await Task.WhenAll(queryTasks)).Where(t => t != null).ToList();
        
        object responseBody;
        if (finalResults.Any())
        {
            responseBody = new
            {
                totalFreeTables = finalResults.Count,
                tables = finalResults
            };
        }
        else
        {
            responseBody = new { tables = finalResults };
        }

        return ResponseCreator.CreateResponse(200, "Success", responseBody);
    }
}