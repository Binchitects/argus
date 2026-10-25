---
name: api-review
title: Review an API
description: Review an HTTP or RPC API design — resources and naming, errors, versioning, pagination, security, consistency. Use when designing or reviewing an API, an OpenAPI document or endpoints.
parameters:
  - { name: api, description: "The API: an OpenAPI document, routes, or code", required: false, default: "the API in this conversation" }
---
Review {{api}} as its clients would meet it.

Check:
- **Resources and naming**: nouns, plural collections, consistent casing, no verbs in paths where a method says it.
- **Methods and status codes**: safe and idempotent methods used right; 201 with Location, 204, 400 vs 422, 401 vs 403, 404, 409.
- **Errors**: one error format (problem+json or similar), useful messages, no internals leaked.
- **Collections**: pagination (cursor or keyset), filtering, sorting, limits.
- **Evolution**: versioning, additive changes, deprecation.
- **Security**: authentication, authorisation per resource, rate limits, input validation, no secrets in URLs.
- **Consistency** with the rest of the API.

Give findings worst first, each with an example of the fix (a corrected path, a response body).
