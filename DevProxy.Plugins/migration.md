# Plugin Migration Guide: From Event-Based to Functional API

This document provides detailed guidance on migrating DevProxy plugins from the old event-based API to the new functional API pattern.

## Overview of API Changes

The DevProxy plugin architecture is transitioning from an event-based model to a functional model for better control flow and testability.

### Old API (Event-Based)
```csharp
public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
{
    // Logic to decide whether to intercept
    if (!ShouldIntercept(e)) 
    {
        return Task.CompletedTask;
    }
    
    // Modify response directly through session
    e.Session.GenericResponse(body, statusCode, headers);
    e.ResponseState.HasBeenSet = true;
    
    return Task.CompletedTask;
}

public override Task BeforeResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
{
    // Process response before it's sent
    return Task.CompletedTask;
}

public override Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
{
    // Process response after it's sent (read-only)
    return Task.CompletedTask;
}
```

### New API (Functional)
```csharp
// For plugins that need to modify requests or responses
public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => async (args, cancellationToken) =>
{
    // Logic to decide whether to intercept
    if (!ShouldIntercept(args.Request)) 
    {
        return PluginResponse.Continue();
    }
    
    // Create and return response
    var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(body)
    };
    
    return PluginResponse.Respond(response);
};

// For guidance plugins that only need to log or analyze requests
public override Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync => async (args, cancellationToken) =>
{
    // Analyze request and provide guidance
    if (ShouldProvideGuidance(args.Request))
    {
        Logger.LogRequest("Guidance message", MessageType.Tip, args.Request);
    }
};

// For plugins that need to modify responses from remote server
public override Func<ResponseArguments, CancellationToken, Task<PluginResponse?>>? OnResponseAsync => async (args, cancellationToken) =>
{
    // Process response and optionally modify it
    // Return null to continue, or PluginResponse to modify
    return null;
};

// For guidance plugins that only need to log or analyze responses
public override Func<ResponseArguments, CancellationToken, Task>? OnResponseLogAsync => async (args, cancellationToken) =>
{
    // Analyze response and provide guidance
    if (ShouldProvideGuidance(args.HttpResponseMessage))
    {
        Logger.LogRequest("Response guidance message", MessageType.Tip, args.HttpRequestMessage, args.RequestId);
    }
};
```

## Key Differences

### 1. Input Arguments
- **Old API:** `ProxyRequestArgs e` and `ProxyResponseArgs e` containing session, response state, and global data
- **New API:** 
  - `RequestArguments args` containing `HttpRequestMessage` and `RequestId`
  - `ResponseArguments args` containing `HttpRequestMessage`, `HttpResponseMessage` and `RequestId`

### 2. Return Values
- **Old API:** `Task` (void) - side effects through `e.Session` and `e.ResponseState`
- **New API:** 
  - `Task<PluginResponse>` for `OnRequestAsync` - explicit return values to control flow
  - `Task` for `OnRequestLogAsync` - read-only logging/analysis of requests
  - `Task<PluginResponse?>` for `OnResponseAsync` - response modification (return null to continue)
  - `Task` for `OnResponseLogAsync` - read-only logging/analysis of responses

### 3. Response Creation
- **Old API:** Direct manipulation of session: `e.Session.GenericResponse(...)`
- **New API:** Create and return `HttpResponseMessage`: `PluginResponse.Respond(response)`

### 4. Flow Control
- **Old API:** Check `e.ResponseState.HasBeenSet` and set it to `true`
- **New API:** Return `PluginResponse.Continue()` or `PluginResponse.Respond(response)`

### 5. Method Selection Guide
Choose the appropriate new API method based on your plugin's behavior:

- **`OnRequestAsync`**: Use for plugins that need to intercept and potentially modify or respond to requests
- **`OnRequestLogAsync`**: Use for guidance plugins that only need to analyze requests and provide logging/guidance (cannot modify requests or responses)
- **`OnResponseAsync`**: Use for plugins that need to modify responses from the remote server
- **`OnResponseLogAsync`**: Use for guidance plugins that only need to analyze responses and provide logging/guidance (cannot modify responses)

## Migration Steps

### Step 1: Determine the Appropriate New Method

**Response Modifying Plugins** → Use `OnRequestAsync`:
- MockResponsePlugin
- AuthPlugin  
- RateLimitingPlugin
- GenericRandomErrorPlugin
- etc.

**Guidance/Analysis Plugins** → Use `OnRequestLogAsync` or `OnResponseLogAsync`:
- CachingGuidancePlugin → `OnRequestLogAsync`
- GraphSdkGuidancePlugin → `OnResponseLogAsync` (analyzes responses from AfterResponseAsync)
- UrlDiscoveryPlugin → `OnRequestLogAsync`
- Most reporting plugins → `OnRequestLogAsync`

**Response Modifying Plugins** → Use `OnResponseAsync`:
- Plugins that need to modify responses from the remote server

### Step 2: Change Method Signature

**For Response Modifying Plugins (OnRequestAsync):**
```csharp
// Before
public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)

// After
public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => async (args, cancellationToken) =>
```

**For Request Guidance Plugins (OnRequestLogAsync):**
```csharp
// Before
public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)

// After
public override Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync => async (args, cancellationToken) =>
```

**For Response Guidance Plugins (OnResponseLogAsync):**
```csharp
// Before
public override Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)

// After
public override Func<ResponseArguments, CancellationToken, Task>? OnResponseLogAsync => async (args, cancellationToken) =>
```

**For Response Modifying Plugins (OnResponseAsync):**
```csharp
// Before
public override Task BeforeResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)

// After
public override Func<ResponseArguments, CancellationToken, Task<PluginResponse?>>? OnResponseAsync => async (args, cancellationToken) =>
```

### Step 3: Update Input Data Access

**Before (Request):**
```csharp
var request = e.Session.HttpClient.Request;
var url = request.RequestUri;
var method = request.Method;
var body = request.BodyString;
```

**After (Request):**
```csharp
var request = args.Request;
var url = request.RequestUri;
var method = request.Method.Method;
var body = await request.Content.ReadAsStringAsync();
```

**Before (Response):**
```csharp
var request = e.Session.HttpClient.Request;
var response = e.Session.HttpClient.Response;
var statusCode = response.StatusCode;
var responseBody = response.BodyString;
```

**After (Response):**
```csharp
var request = args.Request;
var response = args.Response;
var statusCode = response.StatusCode;
var responseBody = await response.Content.ReadAsStringAsync();
```

### Step 4: Update URL Matching Logic

**Before:**
```csharp
if (!e.HasRequestUrlMatch(UrlsToWatch))
{
    Logger.LogRequest("URL not matched", MessageType.Skipped, new(e.Session));
    return Task.CompletedTask;
}
```

**After (OnRequestAsync):**
```csharp
if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
{
    Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request, args.RequestId);
    return PluginResponse.Continue();
}
```

**After (OnRequestLogAsync/OnResponseLogAsync):**
```csharp
if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
{
    Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request, args.RequestId);
    return;
}
```

### Step 5: Update Response State Checking

**Before:**
```csharp
if (e.ResponseState.HasBeenSet)
{
    Logger.LogRequest("Response already set", MessageType.Skipped, new(e.Session));
    return Task.CompletedTask;
}
```

**After:**
```csharp
// Not needed in new API - flow control is handled by return values
// OnRequestAsync: Each plugin returns either Continue() or Respond()
// OnRequestLogAsync/OnResponseLogAsync: Cannot modify responses, so this check is irrelevant
// OnResponseAsync: Return null to continue, or PluginResponse to modify
```

### Step 6: Update Response Creation (OnRequestAsync and OnResponseAsync only)

**Before:**
```csharp
var headers = new List<HttpHeader>
{
    new("Content-Type", "application/json"),
    new("X-Custom", "value")
};

e.Session.GenericResponse(jsonBody, HttpStatusCode.BadRequest, headers);
e.ResponseState.HasBeenSet = true;
```

**After:**
```csharp
var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
{
    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
};
response.Headers.Add("X-Custom", "value");

return PluginResponse.Respond(response);
```

### Step 7: Update Passthrough Logic

**Before:**
```csharp
if (shouldPassThrough)
{
    Logger.LogRequest("Pass through", MessageType.Skipped, new(e.Session));
    return Task.CompletedTask;
}
```

**After (OnRequestAsync/OnResponseAsync):**
```csharp
if (shouldPassThrough)
{
    Logger.LogRequest("Pass through", MessageType.Skipped, args.Request, args.RequestId); // or args.HttpRequestMessage
    return PluginResponse.Continue(); // or return null for OnResponseAsync
}
```

**After (OnRequestLogAsync/OnResponseLogAsync):**
```csharp
if (shouldSkip)
{
    Logger.LogRequest("Skipping analysis", MessageType.Skipped, args.Request, args.RequestId); // or args.HttpRequestMessage
    return;
}
```

## Complete Migration Examples

### Example 1: Response Modifying Plugin (OnRequestAsync)

**Before (Old API):**
```csharp
public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
{
    if (!e.HasRequestUrlMatch(UrlsToWatch))
    {
        return Task.CompletedTask;
    }
    
    if (e.ResponseState.HasBeenSet)
    {
        return Task.CompletedTask;
    }

    if (ShouldFail())
    {
        var error = GetRandomError();
        var body = JsonSerializer.Serialize(error.Body);
        var headers = error.Headers.Select(h => new HttpHeader(h.Name, h.Value));
        
        e.Session.GenericResponse(body, (HttpStatusCode)error.StatusCode, headers);
        e.ResponseState.HasBeenSet = true;
    }

    return Task.CompletedTask;
}
```

**After (New API):**
```csharp
public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => (args, cancellationToken) =>
{
    if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
    {
        return Task.FromResult(PluginResponse.Continue());
    }

    if (!ShouldFail())
    {
        return Task.FromResult(PluginResponse.Continue());
    }

    var error = GetRandomError();
    var response = new HttpResponseMessage((HttpStatusCode)error.StatusCode)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(error.Body), 
            Encoding.UTF8, 
            "application/json"
        )
    };
    
    foreach (var header in error.Headers)
    {
        response.Headers.Add(header.Name, header.Value);
    }

    return Task.FromResult(PluginResponse.Respond(response));
};
```

### Example 2: Request Guidance Plugin (OnRequestLogAsync)

**Before (Old API):**
```csharp
public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
{
    if (!e.HasRequestUrlMatch(UrlsToWatch))
    {
        Logger.LogRequest("URL not matched", MessageType.Skipped, new(e.Session));
        return Task.CompletedTask;
    }

    var request = e.Session.HttpClient.Request;
    if (ShouldProvideGuidance(request))
    {
        Logger.LogRequest("Consider using cache for better performance", MessageType.Tip, new(e.Session));
    }

    return Task.CompletedTask;
}
```

**After (New API):**
```csharp
public override Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync => (args, cancellationToken) =>
{
    if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
    {
        Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request, args.RequestId);
        return Task.CompletedTask;
    }

    if (ShouldProvideGuidance(args.Request))
    {
        Logger.LogRequest("Consider using cache for better performance", MessageType.Tip, args.Request, args.RequestId);
    }

    return Task.CompletedTask;
};
```

### Example 3: Response Guidance Plugin (OnResponseLogAsync)

**Before (Old API):**
```csharp
public override Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
{
    if (!e.HasRequestUrlMatch(UrlsToWatch))
    {
        Logger.LogRequest("URL not matched", MessageType.Skipped, new(e.Session));
        return Task.CompletedTask;
    }

    var response = e.Session.HttpClient.Response;
    if (ShouldProvideGuidance(response))
    {
        Logger.LogRequest("Consider optimizing your API queries", MessageType.Tip, new(e.Session));
    }

    return Task.CompletedTask;
}
```

**After (New API):**
```csharp
public override Func<ResponseArguments, CancellationToken, Task>? OnResponseLogAsync => (args, cancellationToken) =>
{
    if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
    {
        Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request, args.RequestId);
        return Task.CompletedTask;
    }

    if (ShouldProvideGuidance(args.HttpResponseMessage))
    {
        Logger.LogRequest("Consider optimizing your API queries", MessageType.Tip, args.Request, args.RequestId);
    }

    return Task.CompletedTask;
};
```

### Example 4: Plugin with Storage Requirements

**Before (Old API):**
```csharp
public sealed class MyStoragePlugin(
    ILogger<MyStoragePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        // Access global data
        e.GlobalData["RequestCount"] = (int)(e.GlobalData.GetValueOrDefault("RequestCount", 0)) + 1;
        
        // Access session data
        e.SessionData["RequestStartTime"] = DateTime.UtcNow;
        
        return Task.CompletedTask;
    }
    
    public override Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
    {
        // Use session data
        if (e.SessionData.TryGetValue("RequestStartTime", out var startTime))
        {
            var duration = DateTime.UtcNow - (DateTime)startTime;
            Logger.LogInformation("Request took {Duration}ms", duration.TotalMilliseconds);
        }
        
        return Task.CompletedTask;
    }
}
```

**After (New API):**
```csharp
public sealed class MyStoragePlugin(
    ILogger<MyStoragePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyStorage proxyStorage) : BasePlugin(logger, urlsToWatch)
{
    private readonly IProxyStorage _proxyStorage = proxyStorage;
    
    public override Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync => (args, cancellationToken) =>
    {
        // Access global data
        _proxyStorage.GlobalData["RequestCount"] = (int)(_proxyStorage.GlobalData.GetValueOrDefault("RequestCount", 0)) + 1;
        
        // Access request-specific data
        var requestData = _proxyStorage.GetRequestData(args.RequestId);
        requestData["RequestStartTime"] = DateTime.UtcNow;
        
        return Task.CompletedTask;
    };
    
    public override Func<ResponseArguments, CancellationToken, Task>? OnResponseLogAsync => (args, cancellationToken) =>
    {
        // Use request-specific data
        var requestData = _proxyStorage.GetRequestData(args.RequestId);
        if (requestData.TryGetValue("RequestStartTime", out var startTime))
        {
            var duration = DateTime.UtcNow - (DateTime)startTime;
            Logger.LogInformation("Request took {Duration}ms", duration.TotalMilliseconds);
        }
        
        return Task.CompletedTask;
    };
}
```

## Important Notes

### 1. Logging Context
The logging context changes from `LoggingContext(e.Session)` to the appropriate request message:
```csharp
// Old
Logger.LogRequest("Message", MessageType.Info, new LoggingContext(e.Session));

// New (Request-based methods)
Logger.LogRequest("Message", MessageType.Info, args.Request, args.RequestId);

// New (Response-based methods)
Logger.LogRequest("Message", MessageType.Info, args.Request, args.RequestId, args.Response);
```

### 2. Global Data and Session Data
Global data and session data access patterns will need to be reviewed as they may not be available in the new API. These features are now handled through dependency injection using the `IProxyStorage` interface.

**For plugins that need global or request-specific storage:**

Use constructor injection to access the `IProxyStorage` interface:

```csharp
public sealed class MyPlugin(
    ILogger<MyPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyStorage proxyStorage) : BasePlugin(logger, urlsToWatch)
{
    private readonly IProxyStorage _proxyStorage = proxyStorage;

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => (args, cancellationToken) =>
    {
        // Access global data (shared across all requests)
        _proxyStorage.GlobalData["MyKey"] = "MyValue";
        
        // Access request-specific data using the request ID
        var requestData = _proxyStorage.GetRequestData(args.RequestId);
        requestData["RequestSpecificKey"] = "RequestSpecificValue";
        
        return Task.FromResult(PluginResponse.Continue());
    };
}
```

**Migration patterns:**

```csharp
// Old API - Global Data
e.GlobalData["MyKey"] = "MyValue";
var globalValue = e.GlobalData.GetValueOrDefault("MyKey");

// New API - Global Data  
_proxyStorage.GlobalData["MyKey"] = "MyValue";
var globalValue = _proxyStorage.GlobalData.GetValueOrDefault("MyKey");

// Old API - Session Data
e.SessionData["MyKey"] = "MyValue";
var sessionValue = e.SessionData.GetValueOrDefault("MyKey");

// New API - Request Data
var requestData = _proxyStorage.GetRequestData(args.RequestId);
requestData["MyKey"] = "MyValue";
var requestValue = requestData.GetValueOrDefault("MyKey");
```

**Important notes about storage:**
- **Global data** persists across all requests and is shared between all plugins
- **Request data** is specific to a single request and is automatically cleaned up when the request completes
- Request data is accessed using the `RequestId` from the `RequestArguments` or `ResponseArguments`
- For reporting plugins that need to store reports, use global data as shown in `BaseReportingPlugin.StoreReport()`

### 3. New API Benefits
The new API methods provide several advantages:
- **Better Control Flow**: Clear separation between modifying and logging operations
- **Clear Intent**: Method names explicitly indicate their purpose and capabilities
- **Performance**: Logging methods don't block critical paths
- **Separation of Concerns**: Clear distinction between modification and analysis logic

### 4. Async Considerations
All new API methods expect functions that return Tasks, so you can use async/await within the lambda:
```csharp
public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => async (args, cancellationToken) =>
{
    var data = await SomeAsyncOperation(cancellationToken);
    // ... process data
    return PluginResponse.Continue();
};
```

### 5. Error Handling
Error handling should be done within the function and appropriate responses returned:
```csharp
public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => async (args, cancellationToken) =>
{
    try
    {
        // Plugin logic
        return PluginResponse.Continue();
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Error in plugin");
        return PluginResponse.Continue(); // or return an error response
    }
};
```

## Migration instructions

- We have compilation errors, so no need to try to build the project until all plugins are migrated.
- Instead of `string.Equals(args.Request.Method.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase)`, use `args.Request.Method == HttpMethod.Options` for better performance.
- Summarize changes in max two lines

### General Migration Steps

1. Migrate plugin according to the new API method (OnRequestAsync, OnRequestLogAsync, OnResponseAsync, or OnResponseLogAsync).
2. Update inventory.md to reflect the new method, leave the old methods in place using strikethrough.
3. Update migration.md with the new migration status

### For Response Modifying Plugins (OnRequestAsync):
- [ ] Update method signature from `BeforeRequestAsync` to `OnRequestAsync`
- [ ] Change return type from `Task` to `Task<PluginResponse>`
- [ ] Update input parameter from `ProxyRequestArgs` to `RequestArguments`
- [ ] Replace `e.Session.HttpClient.Request` with `args.Request`
- [ ] Replace `e.HasRequestUrlMatch()` with `ProxyUtils.MatchesUrlToWatch()`
- [ ] Remove `e.ResponseState.HasBeenSet` checks
- [ ] Replace `e.Session.GenericResponse()` with `HttpResponseMessage` creation
- [ ] Replace `e.ResponseState.HasBeenSet = true` with `PluginResponse.Respond()`
- [ ] Replace `return Task.CompletedTask` with `PluginResponse.Continue()`
- [ ] Update logging context from `LoggingContext(e.Session)` to `args.Request`
- [ ] Add `IProxyStorage` to constructor if plugin needs global or request-specific data storage
- [ ] Test the migrated plugin thoroughly

### For Request Guidance Plugins (OnRequestLogAsync):
- [ ] Update method signature from `BeforeRequestAsync` to `OnRequestLogAsync`
- [ ] Keep return type as `Task` (no PluginResponse needed)
- [ ] Update input parameter from `ProxyRequestArgs` to `RequestArguments`
- [ ] Replace `e.Session.HttpClient.Request` with `args.Request`
- [ ] Replace `e.HasRequestUrlMatch()` with `ProxyUtils.MatchesUrlToWatch()`
- [ ] Remove any response modification logic (not allowed in OnRequestLogAsync)
- [ ] Update logging context from `LoggingContext(e.Session)` to `args.Request`
- [ ] Add `IProxyStorage` to constructor if plugin needs global or request-specific data storage
- [ ] Test the migrated plugin thoroughly

### For Response Guidance Plugins (OnResponseLogAsync):
- [ ] Update method signature from `AfterResponseAsync` to `OnResponseLogAsync`
- [ ] Keep return type as `Task` (no PluginResponse needed)
- [ ] Update input parameter from `ProxyResponseArgs` to `ResponseArguments`
- [ ] Replace `e.Session.HttpClient.Request` with `args.Request`
- [ ] Replace `e.Session.HttpClient.Response` with `args.Response`
- [ ] Replace `e.HasRequestUrlMatch()` with `ProxyUtils.MatchesUrlToWatch()`
- [ ] Remove any response modification logic (not allowed in OnResponseLogAsync)
- [ ] Update logging context from `LoggingContext(e.Session)` to `args.Request`
- [ ] Add `IProxyStorage` to constructor if plugin needs global or request-specific data storage
- [ ] Test the migrated plugin thoroughly

### For Response Modifying Plugins (OnResponseAsync):
- [ ] Update method signature from `BeforeResponseAsync` to `OnResponseAsync`
- [ ] Change return type from `Task` to `Task<PluginResponse?>`
- [ ] Update input parameter from `ProxyResponseArgs` to `ResponseArguments`
- [ ] Replace `e.Session.HttpClient.Request` with `args.Request`
- [ ] Replace `e.Session.HttpClient.Response` with `args.Response`
- [ ] Replace `e.HasRequestUrlMatch()` with `ProxyUtils.MatchesUrlToWatch()`
- [ ] Remove `e.ResponseState.HasBeenSet` checks
- [ ] Return `null` to continue or `PluginResponse` to modify
- [ ] Update logging context from `LoggingContext(e.Session)` to `args.Request`
- [ ] Add `IProxyStorage` to constructor if plugin needs global or request-specific data storage
- [ ] Test the migrated plugin thoroughly

## Plugin Migration Categorization

Based on the inventory, here's how plugins should be migrated:

### OnRequestAsync (Response Modifying - 17 plugins):
1. AuthPlugin
2. CrudApiPlugin
3. EntraMockResponsePlugin
4. ~~GenericRandomErrorPlugin~~ (MIGRATED)
5. GraphMockResponsePlugin
6. GraphRandomErrorPlugin (already migrated)
7. LanguageModelFailurePlugin
8. LanguageModelRateLimitingPlugin
9. MockRequestPlugin
10. MockResponsePlugin
11. OpenAIMockResponsePlugin
12. RateLimitingPlugin
13. RetryAfterPlugin

### OnRequestLogAsync (Request Guidance/Analysis - 20+ plugins):
1. ApiCenterMinimalPermissionsPlugin
2. ApiCenterOnboardingPlugin
3. ApiCenterProductionVersionPlugin
4. CachingGuidancePlugin (MIGRATED)
5. ExecutionSummaryPlugin
6. GraphClientRequestIdGuidancePlugin (MIGRATED)
7. GraphConnectorGuidancePlugin (MIGRATED)
8. GraphMinimalPermissionsGuidancePlugin
9. GraphMinimalPermissionsPlugin
10. GraphSelectGuidancePlugin (MIGRATED)
11. HttpFileGeneratorPlugin
12. MinimalCsomPermissionsPlugin
13. MinimalPermissionsGuidancePlugin
14. MinimalPermissionsPlugin
15. ~~ODSPSearchGuidancePlugin~~ (MIGRATED)
16. OpenAITelemetryPlugin
17. OpenApiSpecGeneratorPlugin
18. TypeSpecGeneratorPlugin
19. UrlDiscoveryPlugin

### OnResponseLogAsync (Response Guidance/Analysis - plugins analyzing responses):
1. ~~GraphSdkGuidancePlugin~~ (MIGRATED)
2. ~~ODataPagingGuidancePlugin~~ (MIGRATED)

### Special Cases:
- **RewritePlugin**: Modifies requests before they proceed (may need custom handling)
- **DevToolsPlugin**: Uses multiple methods (BeforeRequestAsync, BeforeResponseAsync, AfterResponseAsync, AfterRequestLogAsync)
- **LatencyPlugin**: Adds delay but doesn't modify responses (could use OnRequestLogAsync)

The new API methods enable better control flow by allowing the proxy to handle modification and logging operations separately, improving both performance and code clarity.