using System;
using System.Net.Http;
using System.Text;

namespace Quaver.Steam.Deploy;

public class GameBuild
{
    internal string Name { get; set; }

    internal string QuaverSharedMd5 { get; set; }

    internal string QuaverApiMd5 { get; set; }

    internal string QuaverServerClientMd5 { get; set; }

    // Unused in code but required for API
    internal string QuaverDll { get; set; } = "NULL";

    // Unused in code but required for API
    internal string QuaverServerCommon { get; set; } = " ";

    public override string ToString()
    {
        return $"Quaver.Shared {QuaverSharedMd5} " +
               $"Quaver.API {QuaverApiMd5} " +
               $"Quaver.Server.Client {QuaverServerClientMd5} ";
    }

    public void SendBuild(string secret)
    {
        using HttpClient client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {secret}");

        var content = new MultipartFormDataContent();

        content.Add(new StringContent(Name, Encoding.UTF8), "version");
        content.Add(new StringContent(QuaverSharedMd5, Encoding.UTF8), "quaver_shared_dll");
        content.Add(new StringContent(QuaverApiMd5, Encoding.UTF8), "quaver_api_dll");
        content.Add(new StringContent(QuaverServerClientMd5, Encoding.UTF8), "quaver_server_client_dll");
        content.Add(new StringContent(QuaverDll, Encoding.UTF8), "quaver_dll");
        content.Add(new StringContent(QuaverServerCommon, Encoding.UTF8), "quaver_server_common_dll");

        HttpResponseMessage response =
            client.PostAsync("https://api.quavergame.com/v2/builds", content).Result;

        if (response.IsSuccessStatusCode)
        {
            var result = response.Content.ReadAsStringAsync().Result;
            Console.WriteLine("Response: " + result);
        }
        else
        {
            Console.WriteLine("Error: " + response.StatusCode);
        }
    }
}