﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using System.Xml.Linq;

#nullable enable

namespace Microsoft.DotNet.XHarness.TestRunners.Xunit;

internal class WasmXmlResultWriter
{
    public async static Task WriteResultsToFile(XElement assembliesElement)
    {
        if (OperatingSystem.IsBrowser())
        {
            await Task.Yield();
            GC.Collect();
            await Task.Yield();
            GC.Collect();
        }

        using var ms = new MemoryStream();
        assembliesElement.Save(ms);
        assembliesElement = null!;


        if (OperatingSystem.IsBrowser())
        {
            await Task.Yield();
            GC.Collect();

            try
            {
                using JSObject globalThis = JSHost.GlobalThis;
                if (globalThis.HasProperty("fetch") && globalThis.HasProperty("location") && globalThis.HasProperty("document"))
                {
                    ms.Position = 0;

                    // globalThis.location.origin
                    using JSObject location = globalThis.GetPropertyAsJSObject("location")!;
                    var originURL = location.GetPropertyAsString("origin");

                    using var req = new HttpRequestMessage(HttpMethod.Post, originURL + "/test-results");
                    req.Content = new StreamContent(ms);
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                    req.Content.Headers.ContentLength = ms.Length;

                    using var httpClient = new HttpClient();
                    using var response = await httpClient.SendAsync(req);
                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Finished uploading {ms.Length} bytes of RESULTXML");
                        return;
                    }
                    // otherwise fall back to the console output
                }
            }
            catch (Exception)
            {
                // fall back to the console output
            }
        }

        ms.TryGetBuffer(out var bytes);
        var base64 = Convert.ToBase64String(bytes, Base64FormattingOptions.None);
        Console.WriteLine($"STARTRESULTXML {bytes.Count} {base64} ENDRESULTXML");
        Console.WriteLine($"Finished writing {bytes.Count} bytes of RESULTXML");
    }
}
