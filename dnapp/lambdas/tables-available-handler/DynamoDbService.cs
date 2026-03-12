using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace TablesAvailableHandler;

public class DynamoDbService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tablesTable;
    private readonly string _reservationsTable;

    public DynamoDbService(IAmazonDynamoDB dynamoDb, string tablesTableName, string reservationsTableName)
    {
        _dynamoDb = dynamoDb;
        _tablesTable = tablesTableName;
        _reservationsTable = reservationsTableName;
    }

    public async Task<List<TableDto>> GetTablesByLocationAsync(string locationId, int guests)
    {
        var request = new QueryRequest
        {
            TableName = _tablesTable,
            IndexName = "LocationIndex",
            KeyConditionExpression = "location_id = :loc",
            FilterExpression = "#cap >= :g", 
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#cap", "capacity" } 
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":loc", new AttributeValue { N = locationId } },
                { ":g", new AttributeValue { N = guests.ToString() } }
            }
        };

        var response = await _dynamoDb.QueryAsync(request);
        var tables = new List<TableDto>();

        foreach (var item in response.Items)
        {
            string rawCap = item.ContainsKey("capacity") ? item["capacity"].N : "0";
            if (!int.TryParse(rawCap, out int tableCapacity)) tableCapacity = 0;

            tables.Add(new TableDto
            {
                TableId = item["table_id"].S,
                TableName = item.ContainsKey("table_name") ? item["table_name"].S : $"Table {item["table_id"].S}",
                Capacity = tableCapacity
            });
        }

        return tables;
    }

    public async Task<Dictionary<string, HashSet<string>>> GetBookedSlotsAsync(string locationId, string date)
    {
        var request = new QueryRequest
        {
            TableName = _reservationsTable,
            IndexName = "LocationDateIndex",
            KeyConditionExpression = "location_id = :loc AND reservation_date = :d",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":loc", new AttributeValue { N = locationId } },
                { ":d", new AttributeValue { S = date } }
            }
        };

        var response = await _dynamoDb.QueryAsync(request);
        var bookedSlots = new Dictionary<string, HashSet<string>>();

        foreach (var item in response.Items)
        {
            var tId = item["table_id"].S;
            var timeSlot = item["time_slot"].S;

            if (!bookedSlots.ContainsKey(tId))
            {
                bookedSlots[tId] = new HashSet<string>();
            }
            bookedSlots[tId].Add(timeSlot);
        }

        return bookedSlots;
    }
}