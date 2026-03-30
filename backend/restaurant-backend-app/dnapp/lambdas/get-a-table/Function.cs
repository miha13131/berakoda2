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

namespace GetATable;

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
        var queryParameters = request.QueryStringParameters ?? new Dictionary<string, string>();
        
        if (request.PathParameters == null || !request.PathParameters.TryGetValue("id", out var locationId) || string.IsNullOrWhiteSpace(locationId))
        {
            return ResponseCreator.CreateResponse(400, "Invalid request", "Location id is missing or empty.");
        }
        
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
        
        int guests = 1;
        if (queryParameters.TryGetValue("guests", out var guestsStr))
        {
            if (!int.TryParse(guestsStr, out guests) || guests <= 0 || guests > 20)
            {
                return ResponseCreator.CreateResponse(400, "Invalid request", "Guests must be a valid integer between 1 and 20.");
            }
        }
        
        if (!queryParameters.TryGetValue("date", out var date) || string.IsNullOrWhiteSpace(date))
        {
            return ResponseCreator.CreateResponse(400, "Invalid request", "Date parameter is required.");
        }

        date = date.Trim('"');
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime parsedDate))
        {
            return ResponseCreator.CreateResponse(400, "Invalid request", "Invalid date format. Expected format: YYYY-MM-DD.");
        }
        
        if (parsedDate.Date < DateTime.UtcNow.Date) 
        {
            return ResponseCreator.CreateResponse(400, "Invalid request", "Booking date cannot be in the past.");
        }
        
        if (!queryParameters.TryGetValue("timeStart", out var timeStart) || string.IsNullOrWhiteSpace(timeStart))
        {
            return ResponseCreator.CreateResponse(400, "Invalid request", "timeStart parameter is required.");
        }

        string cleanStartTime = timeStart.Trim('"').Replace(".", "").ToUpper();

        if (!DateTime.TryParseExact(cleanStartTime, "h:mmtt", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime parsedStartTime))
        {
            return ResponseCreator.CreateResponse(400, "Invalid request", "Invalid timeStart format. Expected format like 10:30AM.");
        }

        string startTimeForDb = parsedStartTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        
        var queryRequest = new QueryRequest
        {
            TableName = "Tables",
            KeyConditionExpression = "location_id = :locationId",
            FilterExpression = "#cap >= :guests",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#cap", "capacity" } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":locationId", new AttributeValue { N = locationId } },
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
        
        var tableSlotsResponse = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue> lastKey = null;
        do {
            var resp = await _client.ScanAsync(new ScanRequest { TableName = "Slots", ExclusiveStartKey = lastKey });
            tableSlotsResponse.AddRange(resp.Items);
            lastKey = resp.LastEvaluatedKey?.Count > 0 ? resp.LastEvaluatedKey : null;
        } while (lastKey != null);
        
        context.Logger.LogLine("tableSlotsResponse: " + tableSlotsResponse.Count);

        // Build ordered list of all defined time slots (UTC), filtered by >= requested timeStart.
        // For today's date also exclude slots that are already in the past (UTC).
        var nowUtc = DateTime.UtcNow;
        var isToday = parsedDate.Date == nowUtc.Date;

        var allDefinedSlots = tableSlotsResponse
            .Where(i => i.ContainsKey("slot_id") && i.ContainsKey("slot"))
            .Select(i => new
            {
                SlotId = i["slot_id"].N,
                Slot   = i["slot"].S,
                Label  = i.ContainsKey("label") ? i["label"].S : ""
            })
            .Where(s => string.Compare(s.Slot, startTimeForDb, StringComparison.Ordinal) >= 0)
            .Where(s =>
            {
                if (!isToday) return true;
                return TimeSpan.TryParse(s.Slot, out var slotTime) && parsedDate.Date.Add(slotTime) > nowUtc;
            })
            .OrderBy(s => s.Slot)
            .ToList();

        context.Logger.LogLine("allDefinedSlots after filter: " + allDefinedSlots.Count);

        if (!allDefinedSlots.Any())
        {
            return ResponseCreator.CreateResponse(200, "Success", new { tables = new List<object>() });
        }

        // For each table query all reservations for the full requested date,
        // then available = defined slots minus already-reserved ones.
        var queryTasks = mapped.Select(async table =>
        {
            var reservationsRequest = new QueryRequest
            {
                TableName = "Reservations",
                IndexName = "ReservationIndex",
                KeyConditionExpression = "table_id = :tid AND date_time_start BETWEEN :startOfDay AND :endOfDay",
                FilterExpression = "location_id = :locId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":tid",        new AttributeValue { N = table.id.ToString() } },
                    { ":startOfDay", new AttributeValue { S = $"{date}#00:00" } },
                    { ":endOfDay",   new AttributeValue { S = $"{date}#23:59" } },
                    { ":locId",      new AttributeValue { N = locationId } }
                }
            };

            var reservationsResult = await _client.QueryAsync(reservationsRequest);

            // Build the set of reserved HH:mm (UTC) times for this table on this date.
            var reservedTimes = reservationsResult.Items
                .Where(s => s.ContainsKey("date_time_start"))
                .Select(s =>
                {
                    var parts = s["date_time_start"].S.Split('#');
                    return parts.Length >= 2 ? parts[1] : string.Empty;
                })
                .Where(t => !string.IsNullOrEmpty(t))
                .ToHashSet(StringComparer.Ordinal);

            // Available = all defined slots that are not already reserved.
            var availableSlots = allDefinedSlots
                .Where(s => !reservedTimes.Contains(s.Slot))
                .ToList();

            if (!availableSlots.Any())
                return null;

            return new
            {
                tableName = table.name,
                photo     = table.image,
                isAvailableNow = true,
                slotsCount = availableSlots.Count,
                capacity   = table.capacity,
                slots = availableSlots.Select(s => new
                {
                    id        = s.SlotId,
                    slot      = s.Slot,
                    label     = s.Label,
                    isAvailable = true
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