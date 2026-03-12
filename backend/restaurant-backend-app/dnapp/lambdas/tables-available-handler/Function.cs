using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace TablesAvailableHandler;

public class Function
{
    private readonly DynamoDbService _dbService;

    // Basic time slots from your design
    private readonly List<string> _standardSlots = new()
    {
        "10:30 a.m. - 12:00 p.m.",
        "12:15 p.m. - 1:45 p.m.",
        "2:00 p.m. - 3:30 p.m.",
        "3:45 p.m. - 5:15 p.m.",
        "5:30 p.m. - 7:00 p.m."
    };

    public Function()
    {
        var dynamoDb = new AmazonDynamoDBClient();
        var tablesTableName = Environment.GetEnvironmentVariable("TABLES_TABLE") ?? "Tables";
        var reservationsTableName = Environment.GetEnvironmentVariable("RESERVATIONS_TABLE") ?? "Reservations";
        
        _dbService = new DynamoDbService(dynamoDb, tablesTableName, reservationsTableName);
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            var query = request.QueryStringParameters ?? new Dictionary<string, string>();

            if (!query.TryGetValue("locationId", out var locationId) || !int.TryParse(locationId, out _))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "locationId must be a numeric value.");
            }

            if (!query.TryGetValue("date", out var date) || 
                !DateTime.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsedDate))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "Date must be in yyyy-MM-dd format.");
            }

            if (parsedDate < DateTime.UtcNow.Date)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "Cannot book a table in the past (UTC).");
            }

            if (!query.TryGetValue("guests", out var g) || !int.TryParse(g, out var guests) || guests <= 0 || guests > 20)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "Guests must be a number between 1 and 20.");
            }

            string? specificTime = query.TryGetValue("time", out var t) ? t : null;

            var allTables = await _dbService.GetTablesByLocationAsync(locationId, guests);
            
            if (!allTables.Any())
            {
               return ResponseCreator.CreateResponse(200, "Success", JsonSerializer.Serialize(new List<TableResponseDto>()));
            }

            var bookedSlotsByTable = await _dbService.GetBookedSlotsAsync(locationId, date);

            var responseList = new List<TableResponseDto>();

            foreach (var table in allTables)
            {
                var availableSlots = new List<string>(_standardSlots);

                if (bookedSlotsByTable.TryGetValue(table.TableId, out var bookedSlots))
                {
                    availableSlots.RemoveAll(slot => bookedSlots.Contains(slot));
                }

                if (!string.IsNullOrEmpty(specificTime))
                {
                    availableSlots = availableSlots.Where(s => s.Contains(specificTime)).ToList();
                }

                if (availableSlots.Any())
                {
                    responseList.Add(new TableResponseDto
                    {
                        TableId = table.TableId,
                        TableName = table.TableName,
                        Capacity = table.Capacity,
                        AvailableSlots = availableSlots
                    });
                }
            }

            return ResponseCreator.CreateResponse(200, "Success", JsonSerializer.Serialize(responseList));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error fetching tables: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", "An error occurred.");
        }
    }
}