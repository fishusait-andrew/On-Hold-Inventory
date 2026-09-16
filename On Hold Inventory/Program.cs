using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;



internal static class Program
{
    //fsbuufv
    private class Item
    {
        public int holdingArea { get; set; }
        public int office { get; set; }
        public int consumerShow { get; set; }
        public int holding { get; set; }
        public int warranty { get; set; }
        public long internalId { get; set; }
        public int oldQuantityOnHold { get; set; }
        public int newQuantityOnHold { get; set; }
    }

    private static async Task Main(string[] args)
    {
        try
        {
            string envPath = "C:/Users/Andrew/Desktop/CloudRun Keys/On Hold Inventory/on-hold-inv-prod.env";

            LoadEnvFile(envPath);

            string accountId = Required("NETSUITE_ACCOUNT_ID");
            string clientId = Required("NETSUITE_CLIENT_ID");
            string certificateId = Required("NETSUITE_CERTIFICATE_ID");
            string privateKeyPath = GetPrivateKeyPath(envPath);

            string accountDomain = accountId.Trim().ToLowerInvariant().Replace('_', '-');
            string baseUrl = $"https://{accountDomain}.suitetalk.api.netsuite.com";

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(120)
            };

            await AuthenticateAsync(http, baseUrl, clientId, certificateId, privateKeyPath);

            //items.Add(new Item{});

            //REST is set up here and can begin doing things with NetSuite.
            await ProcessAllItemsAsync(http, baseUrl);



            //use suite ql to getch batches of items their specific fields
            //Holding Area
            //Office
            //Consumer Show
            //These two 
            //Holding
            //Warrenty
            //Current quantity on hold/ field to update : custitem_on_hold_inventory

            //Now I just need to calculate the new value and set it on items.



        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task<List<JsonElement>> GetItemsLocationsAsync(HttpClient http, string baseUrl, string itemIds)
    {
        string query = $"SELECT ail.* FROM AggregateItemLocation ail WHERE ail.item IN ({itemIds}) ORDER BY ail.item, ail.location";

        return await RunSuiteQlAsync(http, baseUrl, query, 1000, true);
    }

    private static async Task<List<JsonElement>> GetInventoryStatusAsync(HttpClient http, string baseUrl, string itemIds)
    {
        string query = $"SELECT ib.* FROM InventoryBalance ib WHERE ib.item IN ({itemIds}) ORDER BY ib.item";

        return await RunSuiteQlAsync(http, baseUrl, query, 1000, true);
    }

    private static async Task ProcessAllItemsAsync(HttpClient http, string baseUrl)
    {
        const int batchSize = 100;//Set to 100

        long lastItemId = 0;
        int batchNumber = 0;

        while (true)
        {
            // Retrieve the next group of item IDs automatically.
            string query = $"SELECT id, custitem_on_hold_inventory FROM item WHERE custitem_item_active_status = 2 AND itemtype = 'InvtPart' AND id > {lastItemId.ToString(System.Globalization.CultureInfo.InvariantCulture)} ORDER BY id"; List<JsonElement> itemRows = await RunSuiteQlAsync(http, baseUrl, query, batchSize, false);

            if (itemRows.Count == 0)
            {
                break;
            }

            List<Item> items = new List<Item>();
            List<string> itemIds = new List<string>();

            foreach (JsonElement row in itemRows)
            {
                long internalId = long.Parse(row.GetProperty("id").ToString(), System.Globalization.CultureInfo.InvariantCulture);

                int oldQuantityOnHold = 0;

                if (row.TryGetProperty("custitem_on_hold_inventory", out JsonElement value) && value.ValueKind != JsonValueKind.Null)
                {
                    oldQuantityOnHold = int.Parse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture);
                }

                items.Add(new Item
                {
                    internalId = internalId,
                    oldQuantityOnHold = oldQuantityOnHold
                });

                itemIds.Add(internalId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            long nextItemId = items[items.Count - 1].internalId;
            string ids = string.Join(",", itemIds);

            batchNumber++;

            try
            {
                Console.WriteLine($"Starting batch {batchNumber}: {items.Count} items.");

                List<JsonElement> locationRows = await GetItemsLocationsAsync(http, baseUrl, ids);
                List<JsonElement> statusRows = await GetInventoryStatusAsync(http, baseUrl, ids);

                // Both result sets are fully retrieved before processing.
                Console.WriteLine($"Retrieved {locationRows.Count} location rows and {statusRows.Count} inventory status rows.");

                foreach (Item item in items)
                {
                    Console.WriteLine($"Item {item.internalId}");

                    foreach (JsonElement locationRow in locationRows)
                    {
                        if (long.Parse(locationRow.GetProperty("item").ToString(), System.Globalization.CultureInfo.InvariantCulture) == item.internalId)
                        {
                            Console.WriteLine($"Location: {locationRow.GetRawText()}");
                        }
                    }

                    foreach (JsonElement statusRow in statusRows)
                    {
                        if (long.Parse(statusRow.GetProperty("item").ToString(), System.Globalization.CultureInfo.InvariantCulture) == item.internalId)
                        {
                            Console.WriteLine($"Inventory status: {statusRow.GetRawText()}");
                        }
                    }
                }

                // Later, put your parsing, calculations, and updates here.
                // Each Item already has its internal ID.
                foreach (var item in items)
                {
                    List<JsonElement> itemLocations = locationRows.FindAll(row => long.Parse(row.GetProperty("item").ToString()) == item.internalId);
                    List<JsonElement> itemStatuses = statusRows.FindAll(row => long.Parse(row.GetProperty("item").ToString()) == item.internalId);

                    await ParseItemsToClass(item, itemLocations, itemStatuses);
                }

                //call update method here to update the items with the new quantity on hold values
                await UpdateItemsAsync(http, baseUrl, items);
                items.Clear();
            }
            catch (Exception ex)
            {
                // Matches your comment: skip a failed batch and continue.
                Console.Error.WriteLine($"Batch {batchNumber} failed. Item IDs: {ids}. Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
            lastItemId = nextItemId;

        }
        
        Console.WriteLine("Finished scanning items.");
    }
    private static async System.Threading.Tasks.Task UpdateItemsAsync(HttpClient http, string baseUrl, List<Item> items)
    {
        foreach (var item in items)
        {
            if(item.oldQuantityOnHold == item.newQuantityOnHold)
            {
                Console.WriteLine($"Item {item.internalId} has no change in quantity on hold. Skipping update.");
                continue;
            }
            string url = $"{baseUrl}/services/rest/record/v1/inventoryItem/{item.internalId}";
            var updateData = new
            {
                custitem_on_hold_inventory = item.newQuantityOnHold
            };
            using var request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Content = new StringContent(JsonSerializer.Serialize(updateData), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            string body = await ReadSuccessfulResponseAsync(response, $"Updating item {item.internalId}");
            Console.WriteLine($"Updated item {item.internalId}: {body}");
        }
    }

    private static async System.Threading.Tasks.Task ParseItemsToClass(Item item, List<JsonElement> locationRows, List<JsonElement> statusRows)
    {
        List<JsonElement> location1ProShop = new List<JsonElement>();
        List<JsonElement> location2Warehouse = new List<JsonElement>();
        List<JsonElement> location3ConsumerShow = new List<JsonElement>();
        List<JsonElement> location4WhHoldingArea = new List<JsonElement>();
        List<JsonElement> location7Office = new List<JsonElement>();

        List<JsonElement> status1Available = new List<JsonElement>();
        List<JsonElement> status4Pending = new List<JsonElement>();
        List<JsonElement> status5Holding = new List<JsonElement>();
        List<JsonElement> status6Warrenty = new List<JsonElement>();
        List<JsonElement> status7ProShop = new List<JsonElement>();

        foreach(JsonElement row in statusRows)
        {
            int statusId = int.Parse(row.GetProperty("inventorystatus").ToString());
            
            if(statusId == 1)
            {
                status1Available.Add(row);
            }

            if (statusId == 4)
            {
                status4Pending.Add(row);
            }

            if (statusId == 5)
            {
                status5Holding.Add(row);
            }

            if (statusId == 6)
            {
                status6Warrenty.Add(row);
            }

            if (statusId == 7)
            {
                status7ProShop.Add(row);
            }
        }

        foreach(JsonElement row in locationRows)
        {
            int locationId = int.Parse(row.GetProperty("location").ToString());

            if(locationId == 1)
            {
                location1ProShop.Add(row);
            }

            if (locationId == 2)
            {
                location2Warehouse.Add(row);
            }

            if (locationId == 3)
            {
                location3ConsumerShow.Add(row);
            }

            if (locationId == 4)
            {
                location4WhHoldingArea.Add(row);
            }

            if (locationId == 7)
            {
                location7Office.Add(row);
            }
        }

        item.holdingArea = SumQuantityOnHand(location4WhHoldingArea);
        item.office = SumQuantityOnHand(location7Office);
        item.consumerShow = SumQuantityOnHand(location3ConsumerShow);

        item.holding = SumQuantityOnHand(status5Holding);
        item.warranty = SumQuantityOnHand(status6Warrenty);
        item.newQuantityOnHold = item.holdingArea + item.office + item.consumerShow + item.holding + item.warranty;

    }
    private static int SumQuantityOnHand(List<JsonElement> rows)
    {
        int total = 0;

        foreach (JsonElement row in rows)
        {
            if (!row.TryGetProperty("quantityonhand", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                total = 0;
            }
            else
            {
                total = int.Parse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return total;
    }

    private static async Task<List<JsonElement>> RunSuiteQlAsync(HttpClient http, string baseUrl, string query, int pageSize, bool getAllPages)
    {
        if (pageSize < 1 || pageSize > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 1000.");
        }

        Console.WriteLine($"[SuiteQL] Starting query: {query}");
        List<JsonElement> rows = new List<JsonElement>();
        int offset = 0;

        while (true)
        {
            string url = $"{baseUrl}/services/rest/query/v1/suiteql?limit={pageSize}&offset={offset}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Prefer", "transient");
            request.Content = new StringContent(JsonSerializer.Serialize(new { q = query }), Encoding.UTF8, "application/json");

            Console.WriteLine($"[SuiteQL] Request: offset={offset}, limit={pageSize}, getAllPages={getAllPages}");
            using var response = await http.SendAsync(request);
            Console.WriteLine($"[SuiteQL] Response: offset={offset}, HTTP={(int)response.StatusCode} {response.ReasonPhrase}");
            string body = await ReadSuccessfulResponseAsync(response, "SuiteQL query");

            using var document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            JsonElement pageItems = root.GetProperty("items");

            foreach (JsonElement row in pageItems.EnumerateArray())
            {
                // Keep the row valid after JsonDocument is disposed.
                rows.Add(row.Clone());
            }

            bool hasMore = root.GetProperty("hasMore").GetBoolean();
            Console.WriteLine($"[SuiteQL] Page: requestedOffset={offset}, rows={pageItems.GetArrayLength()}, accumulatedRows={rows.Count}, hasMore={hasMore}");

            if (!getAllPages || !hasMore)
            {
                break;
            }

            if (pageItems.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("NetSuite reported more results but returned an empty page.");
            }

            offset += pageSize;
        }

        return rows;
    }
    private static async Task AuthenticateAsync(HttpClient http, string baseUrl, string clientId, string certificateId, string privateKeyPath)
    {
        Console.WriteLine("Authenticating with NetSuite...");

        string tokenUrl = $"{baseUrl}/services/rest/auth/oauth2/v1/token";
        string signedJwt = await CreateSignedJwtAsync(tokenUrl, clientId, certificateId, privateKeyPath);

        using var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = signedJwt
        });

        using var response = await http.PostAsync(tokenUrl, tokenForm);
        string body = await ReadSuccessfulResponseAsync(response, "Authentication");

        using var tokenJson = JsonDocument.Parse(body);

        string accessToken = tokenJson.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("NetSuite did not return an access token.");

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        Console.WriteLine("Authentication successful.");
    }

    private static async Task<string> CreateSignedJwtAsync(string tokenUrl, string clientId, string certificateId, string privateKeyPath)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var header = new
        {
            alg = "PS256",
            typ = "JWT",
            kid = certificateId
        };

        var payload = new
        {
            iss = clientId,
            scope = new[] { "rest_webservices" },
            aud = tokenUrl,
            iat = now,
            exp = now + 300,
            jti = Guid.NewGuid().ToString()
        };

        string encodedHeader = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
        string encodedPayload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        string unsignedJwt = $"{encodedHeader}.{encodedPayload}";

        using var rsa = RSA.Create();

        string privateKey = await File.ReadAllTextAsync(privateKeyPath);
        rsa.ImportFromPem(privateKey);

        byte[] signature = rsa.SignData(Encoding.UTF8.GetBytes(unsignedJwt), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        return $"{unsignedJwt}.{Base64Url(signature)}";
    }

    private static async Task<string> ReadSuccessfulResponseAsync(HttpResponseMessage response, string operation)
    {
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{operation} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{body}");
        }

        return body;
    }

    private static string GetPrivateKeyPath(string envPath)
    {
        string privateKeyPath = Required("NETSUITE_PRIVATE_KEY_PATH");

        if (!Path.IsPathRooted(privateKeyPath))
        {
            privateKeyPath = Path.GetFullPath(privateKeyPath, Path.GetDirectoryName(envPath)!);
        }

        return privateKeyPath;
    }

    private static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing configuration: {name}");
        }

        return value.Trim();
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void LoadEnvFile(string path)
    {
        // Cloud Run can supply environment variables directly.
        if (!File.Exists(path))
        {
            Console.WriteLine($"No .env file at {path}; using environment variables.");
            return;
        }

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            // Support single-quoted or double-quoted values.
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            // Existing environment variables take priority.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}