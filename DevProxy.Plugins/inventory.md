# DevProxy Plugins Inventory

This document provides an inventory of all plugins in the DevProxy.Plugins project and their implemented methods. This inventory was created to assist with the migration from the old event-based API to the new functional API.

## ApiCenterMinimalPermissionsPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Read-only (collects API permission data for reporting)

## ApiCenterOnboardingPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Read-only (collects API metadata for reporting)

## ApiCenterProductionVersionPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Read-only (collects API version information for reporting)

## AuthPlugin

**Base Class:** BasePlugin<AuthPluginConfiguration>  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Modifies responses (returns 401/403 for unauthorized requests)

## CachingGuidancePlugin

**Base Class:** BasePlugin<CachingGuidancePluginConfiguration>  
**Methods Implemented:**
- BeforeRequestAsync

**Behavior:** Read-only (analyzes request patterns and provides caching guidance)

## CrudApiPlugin

**Base Class:** BasePlugin<CrudApiConfiguration>  
**Methods Implemented:**
- GetOptions
- OptionsLoaded
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Modifies responses (creates mock CRUD API responses)

## DevToolsPlugin

**Base Class:** BasePlugin<DevToolsConfiguration>  
**Methods Implemented:**
- GetOptions
- OptionsLoaded
- InitializeAsync
- BeforeRequestAsync
- BeforeResponseAsync
- AfterResponseAsync
- AfterRequestLogAsync

**Behavior:** Read-only (captures request/response data for developer tools)

## EntraMockResponsePlugin

**Base Class:** BasePlugin<EntraMockResponseConfiguration>  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Modifies responses (provides mock Entra ID responses)

## ExecutionSummaryPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- GetOptions
- OptionsLoaded
- InitializeAsync
- BeforeRequestAsync
- AfterRecordingStopAsync

**Behavior:** Read-only (collects execution statistics for reporting)

## GenericRandomErrorPlugin

**Base Class:** BasePlugin<GenericRandomErrorConfiguration>  
**Methods Implemented:**
- GetOptions
- OptionsLoaded
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Modifies responses (generates random error responses)

## GraphBetaSupportGuidancePlugin

**Base Class:** BasePlugin  
**Methods Implemented:**
- AfterResponseAsync

**Behavior:** Read-only (provides guidance about beta API usage)

## GraphClientRequestIdGuidancePlugin

**Base Class:** BasePlugin  
**Methods Implemented:**
- AfterResponseAsync

**Behavior:** Read-only (provides guidance about request ID headers)

## GraphConnectorGuidancePlugin

**Base Class:** BasePlugin  
**Methods Implemented:**
- AfterResponseAsync

**Behavior:** Read-only (provides guidance about Graph connector usage)

## GraphMinimalPermissionsGuidancePlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Read-only (analyzes and reports on Graph API permissions)

## GraphMinimalPermissionsPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Read-only (collects Graph API permission data for reporting)

## GraphMockResponsePlugin

**Base Class:** MockResponsePlugin  
**Methods Implemented:**
- BeforeRequestAsync

**Behavior:** Modifies responses (provides mock Graph API responses, including batch processing)

## GraphRandomErrorPlugin

**Base Class:** BasePlugin<GraphRandomErrorConfiguration>  
**Methods Implemented:**
- GetOptions
- OptionsLoaded
- OnRequestAsync (NEW API - MIGRATED)

**Behavior:** Modifies responses (generates random Graph API error responses)

## GraphSdkGuidancePlugin

**Base Class:** BasePlugin  
**Methods Implemented:**
- AfterResponseAsync

**Behavior:** Read-only (provides guidance about using Graph SDKs)

## GraphSelectGuidancePlugin

**Base Class:** BasePlugin  
**Methods Implemented:**
- AfterResponseAsync

**Behavior:** Read-only (provides guidance about Graph $select optimization)

## HttpFileGeneratorPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync
- AfterRecordingStopAsync

**Behavior:** Read-only (generates HTTP files from recorded requests)

## LanguageModelFailurePlugin

**Base Class:** BasePlugin<LanguageModelFailureConfiguration>  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Modifies responses (simulates AI/ML service failures)

## LanguageModelRateLimitingPlugin

**Base Class:** BasePlugin<LanguageModelRateLimitingConfiguration>  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync
- BeforeResponseAsync

**Behavior:** Modifies responses (enforces token-based rate limiting for AI services)

## LatencyPlugin

**Base Class:** BasePlugin<LatencyConfiguration>  
**Methods Implemented:**
- BeforeRequestAsync

**Behavior:** Read-only (adds artificial delay to requests, doesn't modify responses)

## MinimalCsomPermissionsPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Read-only (analyzes SharePoint CSOM permissions for reporting)

## MinimalPermissionsGuidancePlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Read-only (provides permission optimization guidance)

## MinimalPermissionsPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Read-only (collects API permission data for reporting)

## MockGeneratorPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- AfterRecordingStopAsync

**Behavior:** Read-only (generates mock response files from recorded requests)

## MockRequestPlugin

**Base Class:** BasePlugin<MockRequestConfiguration>  
**Methods Implemented:**
- GetOptions
- OptionsLoaded
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Modifies responses (provides mock request/response functionality)

## MockResponsePlugin

**Base Class:** BasePlugin<MockResponseConfiguration>  
**Methods Implemented:**
- GetOptions
- OptionsLoaded
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Modifies responses (provides comprehensive mock response functionality)

## ODSPSearchGuidancePlugin

**Base Class:** BasePlugin  
**Methods Implemented:**
- AfterResponseAsync

**Behavior:** Read-only (provides guidance about SharePoint search optimization)

## ODataPagingGuidancePlugin

**Base Class:** BasePlugin  
**Methods Implemented:**
- AfterResponseAsync

**Behavior:** Read-only (provides guidance about OData paging patterns)

## OpenAIMockResponsePlugin

**Base Class:** BasePlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Modifies responses (provides mock OpenAI API responses using local language models)

## OpenAITelemetryPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Read-only (collects OpenAI API usage telemetry for reporting)

## OpenApiSpecGeneratorPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync
- AfterRecordingStopAsync

**Behavior:** Read-only (generates OpenAPI specifications from recorded requests)

## RateLimitingPlugin

**Base Class:** BasePlugin<RateLimitingConfiguration>  
**Methods Implemented:**
- GetOptions
- OptionsLoaded
- InitializeAsync
- BeforeRequestAsync
- BeforeResponseAsync

**Behavior:** Modifies responses (enforces rate limits and adds rate limit headers)

## RetryAfterPlugin

**Base Class:** BasePlugin  
**Methods Implemented:**
- BeforeRequestAsync

**Behavior:** Modifies responses (throttles requests that don't respect Retry-After headers)

## RewritePlugin

**Base Class:** BasePlugin<RewritePluginConfiguration>  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync

**Behavior:** Modifies requests (rewrites request URLs, doesn't modify responses directly)

## TypeSpecGeneratorPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- InitializeAsync
- BeforeRequestAsync
- AfterRecordingStopAsync

**Behavior:** Read-only (generates TypeSpec definitions from recorded requests)

## UrlDiscoveryPlugin

**Base Class:** BaseReportingPlugin  
**Methods Implemented:**
- BeforeRequestAsync

**Behavior:** Read-only (discovers and reports API URLs)

---

## Summary

- **Total Plugins:** 38
- **Plugins using BeforeRequestAsync:** 32
- **Plugins using BeforeResponseAsync:** 2 (DevToolsPlugin, RateLimitingPlugin)
- **Plugins using AfterResponseAsync:** 9
- **Plugins using AfterRequestLogAsync:** 1 (DevToolsPlugin)
- **Plugins using AfterRecordingStopAsync:** 4
- **Plugins using OnRequestAsync (NEW API):** 1 (GraphRandomErrorPlugin - already migrated)
- **Plugins using OnResponseAsync (NEW API):** 0

### Behavior Classification

**Response Modifying Plugins (17):** These plugins intercept requests and return custom responses
- AuthPlugin
- CrudApiPlugin  
- EntraMockResponsePlugin
- GenericRandomErrorPlugin
- GraphMockResponsePlugin
- GraphRandomErrorPlugin
- LanguageModelFailurePlugin
- LanguageModelRateLimitingPlugin
- MockRequestPlugin
- MockResponsePlugin
- OpenAIMockResponsePlugin
- RateLimitingPlugin
- RetryAfterPlugin

**Request Modifying Plugins (1):** These plugins modify requests before they proceed
- RewritePlugin

**Read-Only Analysis Plugins (20):** These plugins only read request/response data for analysis, guidance, or reporting
- ApiCenterMinimalPermissionsPlugin
- ApiCenterOnboardingPlugin
- ApiCenterProductionVersionPlugin
- CachingGuidancePlugin
- DevToolsPlugin
- ExecutionSummaryPlugin
- GraphBetaSupportGuidancePlugin
- GraphClientRequestIdGuidancePlugin
- GraphConnectorGuidancePlugin
- GraphMinimalPermissionsGuidancePlugin
- GraphMinimalPermissionsPlugin
- GraphSdkGuidancePlugin
- GraphSelectGuidancePlugin
- HttpFileGeneratorPlugin
- LatencyPlugin
- MinimalCsomPermissionsPlugin
- MinimalPermissionsGuidancePlugin
- MinimalPermissionsPlugin
- MockGeneratorPlugin
- ODSPSearchGuidancePlugin
- ODataPagingGuidancePlugin
- OpenAITelemetryPlugin
- OpenApiSpecGeneratorPlugin
- TypeSpecGeneratorPlugin
- UrlDiscoveryPlugin

**Migration Priority:** Response modifying plugins should be migrated first as they have the most complex logic for creating and returning custom responses. Read-only plugins can potentially remain on the old API longer since they don't affect request flow.