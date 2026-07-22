---
name: powershell-gui-designing
description: Use this skill when designing or refining PowerShell GUIs, especially WPF-based apps, including layout, styling, interaction patterns, state handling, and responsive behavior.
---

# PowerShell GUI Designing

This skill provides a practical workflow and design checklist for building clean, maintainable, and user-friendly PowerShell GUI applications.

## When to Use This Skill

Use this skill when the user asks for:

- A PowerShell GUI design or redesign
- Better layout, spacing, and visual hierarchy in a script UI
- Modernization of a WPF/XAML PowerShell interface
- Better UX for forms, dialogs, filters, lists, and action flows
- Refactoring code-behind to improve maintainability
- Theme consistency (dark/light) and reusable style tokens

## Scope and Assumptions

- Primary UI stack: WPF + XAML loaded from PowerShell
- Runtime: Windows PowerShell or PowerShell 7 on Windows
- Focus: practical desktop tooling UIs (admin tools, internal utilities)

## Design Principles

1. Clarity first: every screen should communicate one primary task.
2. Strong hierarchy: emphasize important actions and status, de-emphasize noise.
3. Predictable interactions: controls should behave consistently across views.
4. Fast scanning: labels, groups, and spacing should support quick orientation.
5. Safe actions: destructive actions require confirmation and clear context.

## Recommended Workflow

### 1. Define the Task Flow Before UI Work

Capture:

- User goal (what they need done)
- Main entities (users, roles, groups, files, jobs)
- Primary actions (load, filter, activate, save, remove)
- Error and empty states

Output should be a short flow outline before writing XAML.

### 2. Choose a Layout Skeleton

Prefer these patterns:

- Top command bar + 2-column content area
- Left navigation + right details panel
- Search/filter strip above data lists
- Footer status bar for progress and notifications

Use `Grid` as the default container. Use `StackPanel` only for simple linear groups.

### 3. Establish a Style System Early

Create shared resources for:

- Colors (background, surface, text, muted, accent, danger, success)
- Spacing scale (4/8/12/16/24)
- Border radius equivalents (via control templates)
- Typography sizes and weights

Keep color and spacing values in one resource dictionary to avoid drift.

### 4. Separate View and Logic

- Keep XAML in here-strings or external `.xaml` files.
- Keep event wiring in one initialization section.
- Move domain operations into dedicated PowerShell functions.
- Use one state object (for example, `$AppState`) for shared runtime data.

### 5. Design for States

Each major view should explicitly support:

- Loading
- Ready
- Empty
- Error
- Busy (operation in progress)

Represent state with visible UI cues, not just log output.

### 6. Validate and Guard Input

- Disable primary action buttons until required input is valid.
- Show inline validation near offending controls.
- Avoid modal error spam for routine validation issues.

### 7. Add Feedback Loops

- Show progress for network or long-running calls.
- Provide completion feedback with clear next action.
- Keep a status line for transient operational messages.

## Practical UI Patterns

### Data-heavy pages

- Place filters directly above lists.
- Keep sort and refresh actions close to the dataset.
- Prefer row actions for frequent operations.

### Confirmation dialogs

Include:

- Item name and scope
- What changes immediately
- Reversible vs irreversible note

### Settings panels

- Group by function, not by control type.
- Add helper text below non-obvious options.
- Validate and persist with clear save feedback.

## Maintainability Rules

- Name controls predictably (`BtnRetrieve`, `TxtFilter`, `LstEligible`).
- Avoid duplicate handler code; use small reusable functions.
- Centralize logging and user notifications.
- Keep API/network code out of UI event blocks where possible.

## Accessibility and Readability Baseline

- Ensure sufficient text/background contrast.
- Avoid color-only status indicators; pair with text/icon.
- Support keyboard navigation order for common actions.
- Keep minimum target size practical for desktop clicking.

## Performance Guidance

- Lazy-load heavy datasets.
- Throttle expensive filtering/search handlers.
- Avoid rebuilding large visual trees unnecessarily.
- Offload long tasks and marshal results back to UI thread safely.

## Skill Execution Checklist

When applying this skill, provide:

1. A concise UX diagnosis of current UI issues.
2. A proposed layout and interaction structure.
3. Concrete XAML and PowerShell edits.
4. State, validation, and feedback improvements.
5. A short testing checklist (desktop + small-window behavior).

## Example Prompt Patterns

- "Redesign this WPF window for faster assignment activation workflows."
- "Refactor this PowerShell GUI to separate view state from Graph API calls."
- "Improve dark theme readability and button hierarchy in this XAML layout."
- "Add loading, empty, and error states to this list-based PowerShell GUI."
