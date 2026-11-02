# AI integration — pending changes

Changes to the integration that depend on the AI team, or that were agreed but are not built yet. Add an entry here
when something is to be changed later; it is applied when you say so. Nothing here is implemented unless marked
**done**.

## 1. A property can have several views

**Today:** the AI service accepts one `view` per listing, so a property with several views sends the **first one in
display order** (`views.sort_order`: City, Community, Garden, Golf, Nile, Pool, Sea, Street). The registry already
stores all of them.

**Pending:** the AI team is asked to accept a list of views.

**When it changes:** in `AiListing` replace `string? View` with a list and send every view, in
`AiListingMapper.ToListing`. No database change.

## 2. Stored places are never refreshed

**Today:** a level of the place tree is copied from the AI service the first time it is asked for and then never
updated, so a place the AI service adds later does not appear in a level that is already stored.

**Pending:** decide how to refresh (for example an admin action that re-reads one level, or a periodic job). The AI id
of every row is already stored, so a refresh only adds the rows that are missing.

## 3. Legacy inbound endpoint

`POST /api/property-matches` (the AI service posting matches to us, with `X-Api-Key`) is no longer needed now that
search calls the AI service directly. **Kept until the integration is complete**, then remove it together with
`AiIntegration:ApiKey`, `RequireAiApiKeyAttribute` and its tests.
