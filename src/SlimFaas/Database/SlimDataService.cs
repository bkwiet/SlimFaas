﻿using System.Data;
using System.Net;
using System.Text.Json;
using DotNext;
using DotNext.Net.Cluster.Consensus.Raft;
using RaftNode;
using SlimData;

namespace SlimFaas;
#pragma warning disable CA2252
public class SlimDataService(HttpClient httpClient, IServiceProvider serviceProvider, IRaftCluster cluster)
    : IDatabaseService
{
    private ISupplier<SupplierPayload> SimplePersistentState =>
        serviceProvider.GetRequiredService<ISupplier<SupplierPayload>>();

    public async Task<string> GetAsync(string key)
    {
        await GetAndWaitForLeader();
        await cluster.ApplyReadBarrierAsync();
        SupplierPayload data = SimplePersistentState.Invoke();
        return data.KeyValues.TryGetValue(key, out string? value) ? value : string.Empty;
    }

    public async Task SetAsync(string key, string value)
    {
        MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(value), key);

        EndPoint endpoint = await GetAndWaitForLeader();
        HttpResponseMessage response =
            await httpClient.PostAsync(new Uri($"{endpoint}SlimData/AddKeyValue"), multipart);
        if ((int)response.StatusCode >= 500)
        {
            throw new DataException("Error in calling SlimData HTTP Service");
        }
    }

    public async Task HashSetAsync(string key, IDictionary<string, string> values)
    {
        MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(key), "______key_____");
        foreach (KeyValuePair<string, string> value in values)
        {
            multipart.Add(new StringContent(value.Value), value.Key);
        }

        EndPoint endpoint = await GetAndWaitForLeader();
        HttpResponseMessage response = await httpClient.PostAsync(new Uri($"{endpoint}SlimData/AddHashset"), multipart);
        if ((int)response.StatusCode >= 500)
        {
            throw new DataException("Error in calling SlimData HTTP Service");
        }
    }

    public async Task<IDictionary<string, string>> HashGetAllAsync(string key)
    {
        await GetAndWaitForLeader();
        await cluster.ApplyReadBarrierAsync();
        SupplierPayload data = SimplePersistentState.Invoke();
        return data.Hashsets.TryGetValue(key, out Dictionary<string, string>? value)
            ? (IDictionary<string, string>)value
            : new Dictionary<string, string>();
    }

    public async Task ListLeftPushAsync(string key, string field)
    {
        EndPoint endpoint = await GetAndWaitForLeader();
        HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{endpoint}SlimData/ListLeftPush"));
        MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(field), key);
        request.Content = multipart;
        HttpResponseMessage response = await httpClient.SendAsync(request);
        if ((int)response.StatusCode >= 500)
        {
            throw new DataException("Error in calling SlimData HTTP Service");
        }
    }

    public async Task<IList<string>> ListRightPopAsync(string key, long count = 1)
    {
        EndPoint endpoint = await GetAndWaitForLeader();
        HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{endpoint}SlimData/ListRightPop"));
        MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(count.ToString()), key);

        request.Content = multipart;
        HttpResponseMessage response = await httpClient.SendAsync(request);
        if ((int)response.StatusCode >= 500)
        {
            throw new DataException("Error in calling SlimData HTTP Service");
        }

        string json = await response.Content.ReadAsStringAsync();
        List<string>? result = !string.IsNullOrEmpty(json)
            ? JsonSerializer.Deserialize<ListString>(json,
                ListStringSerializerContext.Default.ListString)
            : new List<string>();

        return result ?? new List<string>();
    }

    public async Task<long> ListLengthAsync(string key)
    {
        await GetAndWaitForLeader();
        await cluster.ApplyReadBarrierAsync();
        SupplierPayload data = SimplePersistentState.Invoke();
        long result = data.Queues.TryGetValue(key, out List<string>? value) ? value.Count : 0L;
        return result;
    }

    private async Task<EndPoint> GetAndWaitForLeader()
    {
        int numberWaitMaximum = 6;
        while (cluster.Leader == null && numberWaitMaximum > 0)
        {
            await Task.Delay(500);
            numberWaitMaximum--;
        }

        if (cluster.Leader == null)
        {
            throw new DataException("No leader found");
        }

        return cluster.Leader.EndPoint;
    }
}
#pragma warning restore CA2252