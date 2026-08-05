using System.Net.Http.Json;

namespace DataIntelligence.IntegrationTests.Api;

/// <summary>Reads a response the way the frontend does, with the API's own serialiser settings.</summary>
internal static class ApiClientExtensions
{
    public static async Task<T> GetJsonAsync<T>(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {url} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var value = await response.Content.ReadFromJsonAsync<T>(DashboardApiFixture.Json);

        Assert.NotNull(value);
        return value!;
    }
}
