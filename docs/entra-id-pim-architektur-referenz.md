# Entra ID PIM Architektur-Referenz

Stand: 2026-07-23  
Scope: Microsoft Entra Rollen-PIM + PIM for Groups (nicht Azure Resource PIM)

## 1) Ziel dieser Referenz

Diese Referenz beschreibt:
- das Daten- und Prozessmodell von PIM fuer Microsoft Entra Rollen
- das Daten- und Prozessmodell von PIM for Groups
- das Zusammenspiel beider Modelle (insbesondere Rollen via Gruppe)
- die wichtigen Graph-Objekte und Attribute
- wie sich Assignment-Typen, Herkuenfte und Abhaengigkeiten eindeutig unterscheiden lassen
- wie das aktuelle Projekt diese Konzepte technisch umsetzt

## 2) Grundmodell: Was ist in PIM immer gleich?

PIM folgt in beiden Bereichen einem aehnlichen Muster:
- Eligibility: Berechtigung zur spaeteren Aktivierung (noch nicht wirksam)
- Activation Request: Antrag auf Aktivierung
- Assignment: aktive bzw. verwaltete Zuweisung (aus Request entstanden)
- Instance: konkret wirksame Instanz mit Start/Ende

Typische Lebenslinie:
1. Admin erstellt Eligible oder Active Assignment (direkt oder zeitgebunden).
2. Nutzer aktiviert Eligible Assignment (selfActivate).
3. PIM erzeugt zeitgebundene aktive Assignment-Instanz.
4. Nach Ablauf oder selfDeactivate wird Zugriff wieder entzogen.

## 3) Architektur-Domaenen

### 3.1 PIM fuer Microsoft Entra Rollen

API-Familie (Graph v1.0):
- /roleManagement/directory/*

Kernobjekte:
- unifiedRoleEligibilityScheduleRequest
- unifiedRoleEligibilitySchedule
- unifiedRoleEligibilityScheduleInstance
- unifiedRoleAssignmentScheduleRequest
- unifiedRoleAssignmentSchedule
- unifiedRoleAssignmentScheduleInstance

Kerngedanke:
- Rollen sind an roleDefinitionId gebunden.
- Scope wird ueber directoryScopeId (oder appScopeId) definiert.
- Aktivierung erzeugt aktive Rolle fuer begrenzte Zeit.

### 3.2 PIM for Groups

API-Familie (Graph v1.0):
- /identityGovernance/privilegedAccess/group/*

Kernobjekte:
- privilegedAccessGroupEligibilityScheduleRequest
- privilegedAccessGroupEligibilitySchedule
- privilegedAccessGroupEligibilityScheduleInstance
- privilegedAccessGroupAssignmentScheduleRequest
- privilegedAccessGroupAssignmentSchedule
- privilegedAccessGroupAssignmentScheduleInstance

Kerngedanke:
- Zugriff wird auf Gruppe (groupId) verwaltet.
- accessId unterscheidet Owner vs Member.
- Aktivierung fuegt Nutzer temporaer als Owner/Member in die Gruppe ein.

## 4) Rollen-PIM und Gruppen-PIM: Zusammenspiel und Abhaengigkeiten

### 4.1 Rolle direkt vs Rolle via Gruppe

Direkte Rolle:
- principalId im Rollenobjekt ist der Benutzer (oder Service Principal / role-assignable group).
- memberType ist typischerweise Direct.

Rolle via Gruppe:
- role-assignable Gruppe traegt die Rolle.
- Nutzer hat (aktiv oder aktiviert) Gruppenmitgliedschaft.
- In Rollen-Assignments kann memberType = Group erscheinen.
- Dann zeigt principalId auf die Gruppe, nicht auf den Endnutzer.

Wichtiger Praxispunkt:
- Wenn eine Rolle nur via Gruppe vererbt ist, fuehrt die effektive Rechte-Eskalation ueber Gruppenaktivierung.
- Technisch ist die Aktivierung dann oft ein Group Assignment Request (nicht zwingend ein direkter Role Assignment Request).

### 4.2 Rolle-zu-Gruppe Designoptionen

Laut Entra Doku sind zwei Muster moeglich:
1. Nutzer aktiv in Gruppe, Gruppe eligible auf Rolle.
2. Gruppe aktiv auf Rolle, Nutzer eligible in Gruppe.

Fuer bestimmte Workloads (z. B. SharePoint/Exchange/Purview) empfiehlt Microsoft Option 1, da Option 2 zu merklicher Bereitstellungs-/Propagation-Verzoegerung fuehren kann.

### 4.3 Unabhaengige Gruppeneigenschaften

Zwei Eigenschaften sind unabhaengig:
- role-assignable (ja/nein)
- PIM for Groups enabled (ja/nein)

Bedeutung:
- Eine Gruppe kann in PIM for Groups aktiviert werden, ohne role-assignable zu sein.
- Um einer Gruppe Entra Rollen zuzuweisen, muss sie role-assignable sein.

## 5) Attribut-Matrix: Rollen-PIM

### 5.1 Eligibility Request (unifiedRoleEligibilityScheduleRequest)

Wichtige Felder:
- id: Request-ID
- action: adminAssign, adminUpdate, adminRemove, selfActivate, selfDeactivate, ...
- principalId: Zielprinzipal
- roleDefinitionId: Rolle
- directoryScopeId / appScopeId: Scope
- justification
- scheduleInfo: startDateTime + expiration (noExpiration/afterDuration/afterDateTime)
- status
- targetScheduleId
- createdBy, createdDateTime, completedDateTime
- ticketInfo

Bedeutung:
- Verwaltungsobjekt fuer Eligible-Zuweisung (Anlegen/Aendern/Entziehen etc.).

### 5.2 Eligibility Schedule (unifiedRoleEligibilitySchedule)

Wichtige Felder:
- id
- principalId
- roleDefinitionId
- directoryScopeId / appScopeId
- memberType: Inherited | Direct | Group
- scheduleInfo
- status
- createdUsing

Bedeutung:
- Persistente Eligible-Zuweisung.

### 5.3 Eligibility Instance (unifiedRoleEligibilityScheduleInstance)

Wichtige Felder:
- id
- principalId
- roleDefinitionId
- directoryScopeId / appScopeId
- memberType
- startDateTime, endDateTime
- roleEligibilityScheduleId

Bedeutung:
- Konkrete wirksame Instanz der Eligibility.

### 5.4 Assignment Request (unifiedRoleAssignmentScheduleRequest)

Wichtige Felder:
- id
- action (inkl. selfActivate/selfDeactivate)
- principalId
- roleDefinitionId
- directoryScopeId / appScopeId
- scheduleInfo
- status
- targetScheduleId
- activatedUsing (Bezug auf Eligibility)
- isValidationOnly
- justification
- ticketInfo

Bedeutung:
- Request fuer aktive Zuweisung oder Aktivierung.

### 5.5 Assignment Schedule (unifiedRoleAssignmentSchedule)

Wichtige Felder:
- id
- principalId
- roleDefinitionId
- directoryScopeId / appScopeId
- assignmentType: Assigned | Activated
- memberType: Inherited | Direct | Group
- scheduleInfo
- status
- activatedUsing
- createdUsing

Bedeutung:
- Persistente aktive Zuweisungsplanung.

### 5.6 Assignment Instance (unifiedRoleAssignmentScheduleInstance)

Wichtige Felder:
- id
- principalId
- roleDefinitionId
- directoryScopeId / appScopeId
- assignmentType: Assigned | Activated
- memberType: Inherited | Direct | Group
- startDateTime, endDateTime
- roleAssignmentScheduleId
- roleAssignmentOriginId
- activatedUsing

Bedeutung:
- Konkret wirksame aktive Rolle (entscheidend fuer aktuelle Zugriffslage).

## 6) Attribut-Matrix: PIM for Groups

### 6.1 Eligibility Request (privilegedAccessGroupEligibilityScheduleRequest)

Wichtige Felder:
- id
- action
- principalId
- groupId
- accessId: member | owner
- scheduleInfo
- status
- targetScheduleId
- justification
- isValidationOnly
- ticketInfo

Bedeutung:
- Request fuer Eligible-Membership/Ownership.

### 6.2 Eligibility Schedule (privilegedAccessGroupEligibilitySchedule)

Wichtige Felder:
- id
- principalId
- groupId
- accessId
- memberType: direct | group
- scheduleInfo
- status
- createdUsing

Bedeutung:
- Persistente Eligible-Beziehung an Gruppe.

### 6.3 Eligibility Instance (privilegedAccessGroupEligibilityScheduleInstance)

Wichtige Felder:
- id
- principalId
- groupId
- accessId
- memberType
- startDateTime, endDateTime
- eligibilityScheduleId

Bedeutung:
- Wirksame Eligibility-Instanz fuer Gruppe.

### 6.4 Assignment Request (privilegedAccessGroupAssignmentScheduleRequest)

Wichtige Felder:
- id
- action (inkl. selfActivate/selfDeactivate)
- principalId
- groupId
- accessId: member | owner
- scheduleInfo
- status
- targetScheduleId
- activatedUsing (Bezug auf Group Eligibility)
- justification
- isValidationOnly
- ticketInfo

Bedeutung:
- Aktivierungs-/Aenderungsrequest fuer aktive Gruppenmitgliedschaft oder -ownership.

### 6.5 Assignment Schedule (privilegedAccessGroupAssignmentSchedule)

Wichtige Felder:
- id
- principalId
- groupId
- accessId
- assignmentType: assigned | activated
- memberType: direct | group
- scheduleInfo
- status
- activatedUsing
- createdUsing

Bedeutung:
- Persistente aktive Membership/Ownership-Zuweisung.

### 6.6 Assignment Instance (privilegedAccessGroupAssignmentScheduleInstance)

Wichtige Felder:
- id
- principalId
- groupId
- accessId
- assignmentType: assigned | activated
- memberType: direct | group
- startDateTime, endDateTime
- assignmentScheduleId
- activatedUsing

Bedeutung:
- Momentan wirksame aktive Membership/Ownership.

## 7) Wie unterscheide ich Dinge eindeutig?

### 7.1 Rollenobjekt vs Gruppenobjekt

Erkennungsregeln:
- Rollenobjekt: hat roleDefinitionId und directoryScopeId/appScopeId.
- Gruppenobjekt: hat groupId und accessId.

### 7.2 Eligible vs Active

Erkennungsregeln:
- Eligibility*-Objekte = noch nicht aktiv nutzbar.
- Assignment*-Objekte = aktiv bzw. aktivierbar/verwaltet.
- Instance-Objekte mit aktueller Zeitlage sind die beste Quelle fuer aktuell wirksam.

### 7.3 Direkt zugewiesen vs via Aktivierung

Erkennungsregeln:
- assignmentType = Assigned: direkt aktive Zuweisung.
- assignmentType = Activated: durch Aktivierung aus Eligibility entstanden.
- activatedUsing != null bestaetigt Herleitung aus Eligibility.

### 7.4 Direkt vs vererbt/via Gruppe

Erkennungsregeln:
- memberType = Direct/direct: direkt.
- memberType = Group/group: Gruppenherkunft.
- memberType = Inherited: vererbt (rollenbezogene Familie).

### 7.5 Owner vs Member (nur Gruppen-PIM)

Erkennungsregeln:
- accessId = owner -> Ownership
- accessId = member -> Membership

## 8) Richtlinien/Policies als harte Randbedingungen

Role/Group Settings koennen Anfragen beschraenken, z. B.:
- MFA Pflicht
- Justification Pflicht
- Approval Pflicht
- max Aktivierungsdauer
- permanent active/eligible erlaubt oder verboten

Folge:
- API-Requests koennen trotz syntaktisch korrekter Payload mit Policy-Fehlern scheitern (z. B. ungueltige Laufzeit).

## 9) Projekt-Mapping: So setzt PIMToolbox es um

Die aktuelle Implementierung nutzt fuer den Benutzerkontext vor allem die Instance-Endpoints:

- Eligible Rollen:
  - GET /roleManagement/directory/roleEligibilityScheduleInstances?$filter=principalId eq '{id}'&$expand=roleDefinition
  - Implementierung in Funktion Get-EligiblePimRoles
- Active Rollen:
  - GET /roleManagement/directory/roleAssignmentScheduleInstances?$filter=principalId eq '{id}'&$expand=roleDefinition
  - Implementierung in Funktion Get-ActivePimRoles
- Eligible Gruppen:
  - GET /identityGovernance/privilegedAccess/group/eligibilityScheduleInstances?$filter=principalId eq '{id}'&$expand=group
  - Implementierung in Funktion Get-EligiblePimGroups
- Active Gruppen:
  - GET /identityGovernance/privilegedAccess/group/assignmentScheduleInstances?$filter=principalId eq '{id}'&$expand=group
  - Implementierung in Funktion Get-ActivePimGroups

Aktivierung in der App:
- Gruppenaktivierung:
  - POST /identityGovernance/privilegedAccess/group/assignmentScheduleRequests
- Rollenaktivierung:
  - POST /roleManagement/directory/roleAssignmentScheduleRequests

Interne Abhaengigkeitsabbildung in PIMToolbox:
- Bei Rollen mit memberType = Group wird eine Abhaengigkeit auf die Quellgruppe modelliert.
- Dafuer werden Felder wie DependencyGroupId, ActivationSource, SourceDisplay genutzt.
- Hierarchieaufbau erfolgt ueber Build-Hierarchy.
- Damit kann die UI erkennen, wann eine Rollenaktivierung auf Gruppenaktivierung umgeleitet werden muss.

## 10) Wichtige Betriebsgrenzen und Sonderfaelle

- Mindestdauer und Entfernen-Sperre:
  - Assignments koennen laut Doku nicht mit weniger als 5 Minuten erstellt werden.
  - Neu gesetzte Assignments koennen innerhalb der ersten 5 Minuten nicht entfernt werden.
- Last owner Schutz bei Gruppen:
  - Letzter aktiver Owner kann nicht entfernt werden.
  - Deaktivierung kann dadurch aussetzen und spaeter erneut versucht werden.
- Global Admin Schutz:
  - Letzte aktive/eligible Global Administrator Assignment wird nicht einfach entfernt, um Tenant-Lockout zu vermeiden.
- Caching/Propagation:
  - Auch wenn PIM aktiv/deaktiv in Sekunden schaltet, koennen Zielsysteme gecachte Berechtigungsbilder haben.

## 11) Entscheidungslogik fuer Analyse im Projekt

Empfohlene Reihenfolge fuer die Auswertung eines Eintrags:
1. Familie bestimmen:
   - roleManagement/directory/* oder privilegedAccess/group/*
2. Objektphase bestimmen:
   - *Request, *Schedule oder *ScheduleInstance
3. Wirksamkeit bestimmen:
   - Assignment/Instance + Zeitfenster + status
4. Herkunft bestimmen:
   - memberType + assignmentType + activatedUsing
5. Bei Gruppenobjekten Zugangstyp bestimmen:
   - accessId (owner/member)
6. Abhaengigkeit bestimmen:
   - Bei RollenmemberType=Group: principalId als Gruppen-ID behandeln und mit PIM-Gruppenstatus korrelieren

## 12) Primarquellen (Microsoft)

Konzept/How-to:
- https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/concept-pim-for-groups
- https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/groups-assign-member-owner
- https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/groups-activate-roles
- https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/pim-how-to-activate-role
- https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/pim-how-to-add-role-to-user

Graph Uebersicht:
- https://learn.microsoft.com/en-us/graph/api/resources/privilegedidentitymanagementv3-overview?view=graph-rest-1.0

Graph Ressourcen (Rollen-PIM):
- https://learn.microsoft.com/en-us/graph/api/resources/unifiedroleeligibilityschedulerequest?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/unifiedroleeligibilityschedule?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/unifiedroleeligibilityscheduleinstance?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/unifiedroleassignmentschedulerequest?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/unifiedroleassignmentschedule?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/unifiedroleassignmentscheduleinstance?view=graph-rest-1.0

Graph Ressourcen (PIM for Groups):
- https://learn.microsoft.com/en-us/graph/api/resources/privilegedaccessgroupeligibilityschedulerequest?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/privilegedaccessgroupeligibilityschedule?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/privilegedaccessgroupeligibilityscheduleinstance?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/privilegedaccessgroupassignmentschedulerequest?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/privilegedaccessgroupassignmentschedule?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/privilegedaccessgroupassignmentscheduleinstance?view=graph-rest-1.0
