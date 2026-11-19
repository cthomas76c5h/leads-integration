/// Every route in docs/api.md. Ids are strings (prefixed random hex).
module LeadIntegrations.Api.Routes

open Oxpecker
open LeadIntegrations.Api.Handlers

let endpoints (c: Ctx) : Endpoint list =
    let g = Http.guard
    [ GET
          [ route "/api/health" (Http.ok {| ok = true |})
            route "/api/catalog" catalog
            route "/api/call-categories" (g callCategories)
            route "/api/call-categories/classify" (g classifyName)
            route "/api/reference-leads" (g referenceLeads)
            route "/api/overview" (g (overview c))
            route "/api/firms" (g (listFirms c))
            routef "/api/firms/{%s}" (fun id -> g (firm c id))
            routef "/api/firms/{%s}/report" (fun id -> g (firmReport c id))
            route "/api/connections" (g (listConnections c))
            route "/api/destinations" (g (listDestinations c))
            route "/api/billing/price-book" (g (priceBook c))
            route "/api/billing/accounts" (g (billingAccounts c))
            routef "/api/firms/{%s}/statement" (fun id -> g (statement c id))
            route "/api/terms" (g (terms c))
            routef "/api/terms/{%i}/acceptances" (fun v -> g (termsAcceptances c v))
            route "/api/portal/terms" (g (termsGate c))
            routef "/api/connections/{%s}/preview" (fun id -> g (preview c id))
            route "/api/leads" (g (listLeads c))
            routef "/api/leads/{%s}" (fun id -> g (lead c id))
            route "/api/deliveries" (g (listDeliveries c))
            route "/api/deliveries/summary" (g (deliverySummary c))
            routef "/api/deliveries/{%s}" (fun id -> g (delivery c id))
            route "/api/received" (g (received c))
            route "/api/setups" (g (listSetups c))
            routef "/api/setups/{%s}/fields" (fun id -> g (setupFields c id))
            routef "/api/client/setups/{%s}" (fun t -> g (clientSetup c t)) ]
      POST
          [ route "/api/firms" (g (createFirm c))
            routef "/api/firms/{%s}/leads" (fun id -> g (sendLead c id))
            routef "/api/firms/{%s}/connections" (fun id -> g (createConnection c id))
            routef "/api/connections/{%s}/test" (fun id -> g (testConnection c id))
            routef "/api/connections/{%s}/send-missed" (fun id -> g (sendMissed c id))
            routef "/api/firms/{%s}/plan" (fun id -> g (changePlan c id))
            route "/api/terms" (g (publishTerms c))
            route "/api/portal/terms/accept" (g (acceptTerms c))
            routef "/api/deliveries/{%s}/replay" (fun id -> g (replay c id))
            route "/api/setups" (g (createSetup c))
            routef "/api/setups/{%s}/{%s}" (fun id action -> g (setupAction c action id))
            routef "/api/client/setups/{%s}/connect" (fun t -> g (clientConnect c t))
            routef "/api/client/setups/{%s}/decision" (fun t -> g (clientDecision c t)) ]
      PATCH
          [ routef "/api/connections/{%s}" (fun id -> g (patchConnection c id))
            routef "/api/setups/{%s}" (fun id -> g (patchSetup c id)) ]
      yield! Receivers.endpoints c.Db ]
