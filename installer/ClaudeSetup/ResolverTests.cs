using System.Net;
using System.Net.Http;

namespace ClaudeSetup;

public static class ResolverTests
{
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action):HttpMessageHandler
    {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;return action(request,token);}}
    private static HttpResponseMessage Good()=>new(HttpStatusCode.OK){Content=new StringContent("{\"Status\":0,\"Answer\":[{\"type\":5,\"data\":\"alias.example\"},{\"type\":1,\"data\":\"8.8.8.8\"},{\"type\":1,\"data\":\"1.1.1.1\"},{\"type\":1,\"data\":\"8.8.8.8\"}]}")};
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("resolver-first-provider-HTTP-failure-uses-same-host-on-backup",async()=> {
            var names=new List<string>();var handler=new Handler((request,_)=>{names.Add(request.RequestUri!.Query);return Task.FromResult(request.RequestUri.Host=="cloudflare-dns.com"?new HttpResponseMessage(HttpStatusCode.ServiceUnavailable):Good());});
            using var client=new HttpClient(handler);var addresses=await NodeEndpointResolver.Resolve("same-host.example",CancellationToken.None,client);
            Assert(handler.Calls==2&&names.All(q=>q.Contains("name=same-host.example"))&&addresses.SequenceEqual(new[]{"1.1.1.1","8.8.8.8"}),"changed selected endpoint or lost fallback");
        });
        await check("resolver-all-failures-are-bounded-and-do-not-expose-host",async()=> {
            var handler=new Handler((request,_)=>Task.FromResult(new HttpResponseMessage(request.RequestUri!.Host=="cloudflare-dns.com"?HttpStatusCode.Forbidden:HttpStatusCode.BadGateway)));
            using var client=new HttpClient(handler);
            try{await NodeEndpointResolver.Resolve("private-fixture.example",CancellationToken.None,client);throw new Exception("Expected DNS failure");}
            catch(SetupException error){Assert(handler.Calls==2&&error.Message.Contains("Cloudflare:HTTP_403")&&error.Message.Contains("Google:HTTP_502")&&!error.Message.Contains("private-fixture"),"provider diagnostics missing or private host escaped");}
        });
        await check("resolver-private-address-cannot-be-bypassed-through-backup",async()=> {
            var handler=new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"Status\":0,\"Answer\":[{\"type\":1,\"data\":\"10.0.0.1\"}]}")}));
            using var client=new HttpClient(handler);
            try{await NodeEndpointResolver.Resolve("fixture.example",CancellationToken.None,client);throw new Exception("Expected private address rejection");}
            catch(SetupException){Assert(handler.Calls==1,"private address bypassed");}
        });
        await check("resolver-IP-literal-does-not-call-any-HTTP-service",async()=> {
            var handler=new Handler((_,_)=>throw new Exception("No HTTP allowed"));using var client=new HttpClient(handler);
            var result=await NodeEndpointResolver.Resolve("1.1.1.1",CancellationToken.None,client);Assert(result.Single()=="1.1.1.1"&&handler.Calls==0,"literal unexpectedly resolved through HTTP");
        });
        await check("resolver-user-cancellation-does-not-start-or-retry-request",async()=> {
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();var handler=new Handler((_,_)=>Task.FromResult(Good()));using var client=new HttpClient(handler);
            try{await NodeEndpointResolver.Resolve("fixture.example",cancellation.Token,client);throw new Exception("Expected cancellation");}catch(OperationCanceledException){Assert(handler.Calls==0,"request made after cancellation");}
        });
        await check("resolver-invalid-JSON-can-use-backup-without-rewriting-node",async()=> {
            var handler=new Handler((request,_)=>Task.FromResult(request.RequestUri!.Host=="cloudflare-dns.com"?new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("not-json-private-fixture")}:Good()));using var client=new HttpClient(handler);
            var result=await NodeEndpointResolver.Resolve("fixture.example",CancellationToken.None,client);Assert(result.Length==2&&handler.Calls==2,"invalid provider JSON blocked backup");
        });
        await check("resolver-provider-timeout-is-bounded-and-separate-from-cancel",async()=> {
            var handler=new Handler(async(request,token)=>{if(request.RequestUri!.Host=="cloudflare-dns.com")await Task.Delay(Timeout.Infinite,token);return Good();});using var client=new HttpClient(handler);
            var result=await NodeEndpointResolver.Resolve("fixture.example",CancellationToken.None,client,TimeSpan.FromMilliseconds(30));Assert(result.Length==2&&handler.Calls==2,"provider timeout lost fallback");
        });
        await check("resolver-HTTP-exception-status-is-safe-and-specific",()=> {
            var failure=NodeEndpointResolver.HttpFailure(new HttpRequestException("PRIVATE_FIXTURE",null,HttpStatusCode.Unauthorized));
            Assert(failure=="HTTP_401"&&!failure.Contains("PRIVATE_FIXTURE"),"HTTP exception status hidden or native message escaped");return Task.CompletedTask;
        });
        await check("resolver-malformed-host-is-rejected-before-HTTP",async()=> {
            var handler=new Handler((_,_)=>throw new Exception("No HTTP allowed"));using var client=new HttpClient(handler);
            try{await NodeEndpointResolver.Resolve("bad.example:443",CancellationToken.None,client);throw new Exception("Expected malformed server rejection");}catch(SetupException){Assert(handler.Calls==0,"malformed hostname sent to DNS service");}
        });
    }
}
