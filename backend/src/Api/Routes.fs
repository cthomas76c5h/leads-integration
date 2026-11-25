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
            // The firm portal, signed in (Portal.fs): only the token's own firm.
            route "/api/portal/leads" (g (Portal.list c))
            routef "/api/portal/leads/{%s}" (fun id -> g (Portal.read c id))
            routef "/api/portal/leads/{%s}/export" (fun id -> g (Portal.export c id))
            route "/api/access/refusals" (g (Portal.refusals c))
            routef "/api/connections/{%s}/preview" (fun id -> g (preview c id))
            route "/api/leads" (g (listLeads c))
            routef "/api/leads/{%s}" (fun id -> g (lead c id))
            routef "/api/leads/{%s}/classification" (fun id -> g (classification c id))
            routef "/api/leads/{%s}/contract" (fun id -> g (leadContract c id))
            route "/api/firm-aliases" (g (firmAliases c))
            routef "/api/leads/{%s}/billing" (fun id -> g (leadBilling c id))
            route "/api/deliveries" (g (listDeliveries c))
            route "/api/deliveries/summary" (g (deliverySummary c))
            routef "/api/deliveries/{%s}" (fun id -> g (delivery c id))
            route "/api/received" (g (received c))
            route "/api/setups" (g (listSetups c))
            routef "/api/setups/{%s}/fields" (fun id -> g (setupFields c id))
            routef "/api/client/setups/{%s}" (fun t -> g (clientSetup c t))
            // A person at the firm, from the link in an email or SMS: public, the token is the permission.
            routef "/ack/{%s}" (fun id -> g (ackPage c id)) ]
      POST
          [ route "/api/firms" (g (createFirm c))
            routef "/api/firms/{%s}/leads" (fun id -> g (sendLead c id))
            // A lead in a versioned contract, as the call center publishes it (docs/contracts/intake-lead/).
            route "/api/ingest/intake-lead/draft-2" (g (ingestDraft2 c))
            routef "/api/firms/{%s}/connections" (fun id -> g (createConnection c id))
            routef "/api/connections/{%s}/test" (fun id -> g (testConnection c id))
            routef "/api/connections/{%s}/send-missed" (fun id -> g (sendMissed c id))
            routef "/api/connections/{%s}/routing" (fun id -> g (proposeRouting c id))
            routef "/api/leads/{%s}/classification" (fun id -> g (correctClassification c id))
            routef "/api/connections/{%s}/routing/approve" (fun id -> g (approveRouting c id))
            routef "/api/firms/{%s}/plan" (fun id -> g (changePlan c id))
            route "/api/terms" (g (publishTerms c))
            route "/api/portal/terms/accept" (g (acceptTerms c))
            routef "/api/portal/leads/{%s}/resend" (fun id -> g (Portal.resend c id))
            routef "/api/firms/{%s}/users" (fun id -> g (Portal.issue c id))
            routef "/api/deliveries/{%s}/replay" (fun id -> g (replay c id))
            routef "/api/deliveries/{%s}/resolve" (fun id -> g (resolveDelivery c id))
            route "/api/setups" (g (createSetup c))
            routef "/api/setups/{%s}/{%s}" (fun id action -> g (setupAction c action id))
            routef "/api/client/setups/{%s}/connect" (fun t -> g (clientConnect c t))
            routef "/api/client/setups/{%s}/decision" (fun t -> g (clientDecision c t))
            routef "/ack/{%s}" (fun id -> g (ackConfirm c id)) ]
      PUT [ routef "/api/firm-aliases/{%s}" (fun alias -> g (setFirmAlias c alias)) ]
      PATCH
          [ routef "/api/firms/{%s}" (fun id -> g (patchFirm c id))
            routef "/api/connections/{%s}" (fun id -> g (patchConnection c id))
            routef "/api/setups/{%s}" (fun id -> g (patchSetup c id)) ]
      yield! Receivers.endpoints c.Db
      // Scenario runs only (docs/api.md, "Test control"); Config refuses it against the lab's database.
      if c.Cfg.TestControl then yield! TestControl.endpoints c ]
