# HTML Sanitization Relaxation Plan

## Goal

Improve HTML email fidelity incrementally without weakening the current privacy and active-content posture.

## Current posture

- HTML is sanitized before storage and again on some fallback/render paths.
- WebView2 scripting is disabled.
- Remote media loads remain blocked by default.
- `script`, `style`, `iframe`, `object`, `embed`, `link`, `form`, and similar active or fetch-capable elements are removed entirely.
- The original sanitizer also removed all inline styles, which preserved safety but badly degraded layout fidelity.

## Phase 1 decisions

- Keep remote content blocked by default.
- Keep `<style>` blocks stripped.
- Keep `<link rel="stylesheet">` stripped.
- Do not attempt `cid:` inline-image rendering yet.
- Allow a constrained subset of inline CSS properties and common presentational HTML attributes used by email markup.

## Phase 1 scope

1. Refactor the sanitizer into explicit policy layers:
   - element filtering
   - attribute filtering
   - URL filtering
   - CSS filtering
2. Preserve the current dangerous-element denylist.
3. Replace the blanket `style` stripping rule with a constrained inline CSS sanitizer.
4. Expand low-risk presentational attributes used by email layout:
   - `align`, `valign`, `width`, `height`
   - `cellpadding`, `cellspacing`, `colspan`, `rowspan`, `bgcolor`, `border`
   - `class`, `lang`, `dir`
5. Keep blocked-resource accounting in place.
6. Persist a sanitizer policy version with each cached body so archived messages can be lazily re-sanitized and re-persisted when the policy changes.

## Deferred work

### Phase 2 candidates

- per-message remote image opt-in

### Later possibilities

- sanitized `<style>` block support
- trusted-sender remote image loading

## Test strategy

Expand sanitizer tests in four buckets:

1. Existing safety behavior:
   - scripts removed
   - remote images blocked
   - private-network URLs blocked
   - `javascript:` links removed
   - `data:` images allowed
   - `<link rel="stylesheet">` removed
2. Fidelity-preserving cases:
   - inline font/color/spacing styles survive
   - table layout attributes survive
   - image width/height survive
3. CSS safety:
   - `url(...)`, `expression(...)`, `behavior(...)`, `javascript:`, `data:` rejected in styles
   - mixed safe/unsafe declarations keep only safe declarations
4. Realistic fixture snippets:
   - transactional email
   - newsletter/table layout
   - marketing email with heavy inline styles

## Rationale for keeping remote content blocked

Remote images and external media remain the largest privacy risk in typical email rendering because they enable tracking pixels and sender-controlled fetches. Phase 1 is focused on presentation fidelity that can be recovered locally, without changing that privacy model.

## Current status update

- `cid:` inline image rendering is now allowed for `img[src]` and resolved only from locally stored attachments.
- This does not enable remote content; it only replaces `cid:` references with local attachment data at render time.
