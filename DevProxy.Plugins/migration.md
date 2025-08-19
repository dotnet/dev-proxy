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
public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => 
    async (args, cancellationToken) =>
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
public override Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync => 
    async (args, cancellationToken) =>
{
    // Analyze request and provide guidance
    if (ShouldProvideGuidance(args.Request))
    {
        Logger.LogRequest("Guidance message", MessageType.Tip, args.Request);
    }
};

// For plugins that need to modify responses from remote server
public override Func<ResponseEventArguments, CancellationToken, Task<ResponseEventResponse?>>? OnResponseAsync => 
    async (args, cancellationToken) =>
{
    // Process response and optionally modify it
    // Return null to continue, or ResponseEventResponse to modify
    return null;
};
```

## Key Differences

### 1. Input Arguments
- **Old API:** `ProxyRequestArgs e` containing session, response state, and global data
- **New API:** `RequestArguments args` containing `HttpRequestMessage` and `RequestId`

### 2. Return Values
- **Old API:** `Task` (void) - side effects through `e.Session` and `e.ResponseState`
- **New API:** 
  - `Task<PluginResponse>` for `OnRequestAsync` - explicit return values to control flow
  - `Task` for `OnRequestLogAsync` - read-only logging/analysis
  - `Task<ResponseEventResponse?>` for `OnResponseAsync` - response modification

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

## Migration Steps

### Step 1: Determine the Appropriate New Method

**Response Modifying Plugins** ? Use `OnRequestAsync`:
- MockResponsePlugin
- AuthPlugin  
- RateLimitingPlugin
- GenericRandomErrorPlugin
- etc.

**Guidance/Analysis Plugins** ? Use `OnRequestLogAsync`:
- CachingGuidancePlugin
- GraphSdkGuidancePlugin (when migrated from AfterResponseAsync)
- UrlDiscoveryPlugin
- Most reporting plugins
- etc.

### Step 2: Change Method Signature

**For Response Modifying Plugins (OnRequestAsync):**
```csharp
// Before
public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)

// After
public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => 
    async (args, cancellationToken) =>
```

**For Guidance Plugins (OnRequestLogAsync):**
```csharp
// Before
public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)

// After
public override Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync => 
    async (args, cancellationToken) =>
```

### Step 3: Update Input Data Access

**Before:**
```csharp
var request = e.Session.HttpClient.Request;
var url = request.RequestUri;
var method = request.Method;
var body = request.BodyString;
```

**After:**
```csharp
var request = args.Request;
var url = request.RequestUri;
var method = request.Method.Method;
var body = await request.Content.ReadAsStringAsync();
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
    Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
    return PluginResponse.Continue();
}
```

**After (OnRequestLogAsync):**
```csharp
if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
{
    Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
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
// OnRequestLogAsync: Cannot modify responses, so this check is irrelevant
```

### Step 6: Update Response Creation (OnRequestAsync only)

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

**After (OnRequestAsync):**
```csharp
if (shouldPassThrough)
{
    Logger.LogRequest("Pass through", MessageType.Skipped, args.Request);
    return PluginResponse.Continue();
}
```

**After (OnRequestLogAsync):**
```csharp
if (shouldSkip)
{
    Logger.LogRequest("Skipping analysis", MessageType.Skipped, args.Request);
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
public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => 
    (args, cancellationToken) =>
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

### Example 2: Guidance Plugin (OnRequestLogAsync)

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
public override Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync => 
    (args, cancellationToken) =>
{
    if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
    {
        Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
        return Task.CompletedTask;
    }

    if (ShouldProvideGuidance(args.Request))
    {
        Logger.LogRequest("Consider using cache for better performance", MessageType.Tip, args.Request);
    }

    return Task.CompletedTask;
};
```

## Important Notes

### 1. Logging Context
The logging context changes from `LoggingContext(e.Session)` to just the `HttpRequestMessage`:
```csharp
// Old
Logger.LogRequest("Message", MessageType.Info, new LoggingContext(e.Session));

// New  
Logger.LogRequest("Message", MessageType.Info, args.Request);
```

### 2. Global Data and Session Data
Global data and session data access patterns will need to be reviewed as they may not be available in the new API. These features may be handled differently or through dependency injection.

### 3. OnRequestLogAsync Benefits
The new `OnRequestLogAsync` method provides several advantages for guidance plugins:
- **Better Control Flow**: The proxy can respond quickly without waiting for guidance analysis
- **Clear Intent**: Explicitly indicates the plugin is read-only and cannot modify requests/responses
- **Performance**: Guidance plugins don't block the request pipeline
- **Separation of Concerns**: Clearly separates modification logic from analysis logic

### 4. Async Considerations
Both new API methods expect functions that return Tasks, so you can use async/await within the lambda:
```csharp
public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => 
    async (args, cancellationToken) =>
{
    var data = await SomeAsyncOperation(cancellationToken);
    // ... process data
    return PluginResponse.Continue();
};
```

### 5. Error Handling
Error handling should be done within the function and appropriate responses returned:
```csharp
public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => 
    async (args, cancellationToken) =>
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

## Migration Checklist

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
- [ ] Test the migrated plugin thoroughly

### For Guidance Plugins (OnRequestLogAsync):
- [ ] Update method signature from `BeforeRequestAsync` to `OnRequestLogAsync`
- [ ] Keep return type as `Task` (no PluginResponse needed)
- [ ] Update input parameter from `ProxyRequestArgs` to `RequestArguments`
- [ ] Replace `e.Session.HttpClient.Request` with `args.Request`
- [ ] Replace `e.HasRequestUrlMatch()` with `ProxyUtils.MatchesUrlToWatch()`
- [ ] Remove any response modification logic (not allowed in OnRequestLogAsync)
- [ ] Update logging context from `LoggingContext(e.Session)` to `args.Request`
- [ ] Test the migrated plugin thoroughly

## Plugin Migration Categorization

Based on the inventory, here's how plugins should be migrated:

### OnRequestAsync (Response Modifying - 17 plugins):
1. AuthPlugin
2. CrudApiPlugin
3. EntraMockResponsePlugin
4. GenericRandomErrorPlugin
5. GraphMockResponsePlugin
6. GraphRandomErrorPlugin (already migrated)
7. LanguageModelFailurePlugin
8. LanguageModelRateLimitingPlugin
9. MockRequestPlugin
10. MockResponsePlugin
11. OpenAIMockResponsePlugin
12. RateLimitingPlugin
13. RetryAfterPlugin

### OnRequestLogAsync (Guidance/Analysis - 20+ plugins):
1. ApiCenterMinimalPermissionsPlugin
2. ApiCenterOnboardingPlugin
3. ApiCenterProductionVersionPlugin
4. CachingGuidancePlugin
5. ExecutionSummaryPlugin
6. GraphMinimalPermissionsGuidancePlugin
7. GraphMinimalPermissionsPlugin
8. HttpFileGeneratorPlugin
9. MinimalCsomPermissionsPlugin
10. MinimalPermissionsGuidancePlugin
11. MinimalPermissionsPlugin
12. OpenAITelemetryPlugin
13. OpenApiSpecGeneratorPlugin
14. TypeSpecGeneratorPlugin
15. UrlDiscoveryPlugin

### Special Cases:
- **RewritePlugin**: Modifies requests before they proceed (may need custom handling)
- **DevToolsPlugin**: Uses multiple methods (BeforeRequestAsync, BeforeResponseAsync, AfterResponseAsync, AfterRequestLogAsync)
- **LatencyPlugin**: Adds delay but doesn't modify responses (could use OnRequestLogAsync)

### Plugins using AfterResponseAsync (may migrate to OnResponseAsync):
- GraphBetaSupportGuidancePlugin
- GraphClientRequestIdGuidancePlugin
- GraphConnectorGuidancePlugin
- GraphSdkGuidancePlugin
- GraphSelectGuidancePlugin
- ODSPSearchGuidancePlugin
- ODataPagingGuidancePlugin

The `OnRequestLogAsync` method enables better control flow by allowing the proxy to respond quickly while still providing comprehensive guidance and analysis capabilities.