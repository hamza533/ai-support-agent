# Google Stitch prompt – Brightline support agent

Paste everything inside the code block into Stitch and choose **Web**. Export each screen as a PNG at 1920×1080 (plus HTML/code if offered) and save it here as `01-help-center.png` … `07-settings.png`. The four chat states are `02a`–`02d`.

```
Design a responsive web app (browser-based, designed for laptop/desktop screens first at 1440px wide, customer chat also works on mobile) for "Brightline Electronics" — a fictional online electronics retailer — with an AI support agent that answers from help-centre documents with citations and takes real actions (order lookup, return requests, support tickets) through the company's existing systems, always asking the customer to confirm before changing anything. Two areas: a customer-facing Help Center with chat, and a staff Admin Console. Use ONE consistent design system and realistic sample data (no lorem ipsum).

SAMPLE STORY (use these exact details everywhere)
Customer: Maya Thompson, email maya.t@example.com. Order BL-10482, delivered 9 days ago: 1 × Aurora Wireless Headphones ($149), 1 × USB-C Charger 65W ($39). Return policy: 30 days, unopened or defective items. Maya asks whether she can return the headphones because the left ear cuts out. The agent asks for her order number and email, verifies her, checks eligibility ("Eligible — delivered 9 days ago, within 30-day window, defect reported"), shows a confirmation card, she confirms, return request RR-2291 is created, and a support ticket TK-5310 is opened for the defect.

GLOBAL STYLE
Clean, trustworthy, modern style in the spirit of shadcn/ui (Tailwind CSS): cards, badges, tabs, tables, dialogs, inputs, toasts, skeleton loaders. Light theme with dark-mode support; neutral zinc grays with one blue accent; success green, warning amber, danger red. Customer area: friendly, spacious. Admin area: left sidebar (Overview, Conversations, Tickets, Documents, Settings), top bar with "Brightline Admin", environment badge "Demo", user menu. Rounded corners, subtle shadows, readable 14–15px text, accessible contrast, simple line icons. No stock photos of people; initials avatars only.

SCREEN 1 — HELP CENTER HOME (customer)
Header with Brightline logo wordmark, search bar "Search help articles", and "Chat with support" button. Popular topics cards (Returns & refunds, Delivery, Warranty, Payments). A short list of popular articles. Bottom-right floating "Ask Brightline Assistant" chat button.

SCREEN 2 — CUSTOMER CHAT, 4 STATES (create 4 frames of the same chat panel, docked on the right of the Help Center)
2a Policy answer: Maya asks "Can I return headphones if one side stopped working?" The assistant streams an answer citing the return policy with numbered citations [1] [2] and small source cards (article title + link).
2b Verification: the assistant asks for order number and email; Maya provides BL-10482 and maya.t@example.com; a small green "Verified ✓" chip appears in the chat header; the assistant shows an order summary card (items, delivery date, status Delivered).
2c Confirmation card: an inline action card titled "Confirm return request" listing exactly: "Return 1 × Aurora Wireless Headphones from order BL-10482 · Reason: defect (left ear cuts out) · Refund to original payment method per policy". Buttons "Confirm" (primary) and "Cancel". Small note "Nothing changes until you confirm."
2d Done: success toast and message "Return RR-2291 created. I've also opened ticket TK-5310 for the defect — our team replies within 1 business day." A collapsed "What I did" section listing tool steps with checkmarks (Searched help articles, Verified order, Checked eligibility, Created return request, Created support ticket). Also include a small edge state: "I can't reach our order system right now — would you like me to create a ticket instead?" with a button.

SCREEN 3 — ADMIN OVERVIEW
Stat cards: Conversations today 64, Resolved without a human 71%, Tickets created 9, Avg first response 1.2 s. A line chart of conversations per day. A table "Recent actions by the agent" (time, customer masked, action e.g. "Return request created", status). A "Needs attention" list of escalated tickets.

SCREEN 4 — CONVERSATION DETAIL (admin)
Left: conversations list with search and filters (Resolved, Escalated, Unverified). Right: Maya's conversation transcript with citations shown inline; a right-hand "Agent trace" timeline: each tool call as a card with tool name (search_knowledge_base, get_order_status, check_return_eligibility, create_return_request, create_support_ticket), masked inputs, result status, duration in ms, and approval badge (Not required / Approved by customer). Footer stats: tokens in/out, time to first token, model.

SCREEN 5 — TICKETS
Table: Ticket ID, subject, customer (masked), category, priority badge, status (Open, In progress, Resolved), created time, linked conversation. Ticket detail drawer for TK-5310 showing the AI-written summary, linked order and return, status change dropdown, internal notes.

SCREEN 6 — DOCUMENTS & INGESTION
Upload area (PDF, DOCX, Markdown). Documents table: title, type icon, chunks, version, status (Indexed, Processing, Failed), last updated, actions (Re-index, Delete). An "Ingestion jobs" panel with progress bars and statuses (Queued, Running, Succeeded, Failed with Retry).

SCREEN 7 — SETTINGS
Tabs: Agent (system prompt editor, model selector showing "Gemini Flash (dev) / Azure OpenAI (prod)", temperature), Policies (return window days 30, eligible categories, max items per return), Approvals (toggles: require confirmation for return requests, for support tickets — both locked ON with a note "Required for actions that change data"), Access (staff users table with roles Admin/Staff, "Sign in with Microsoft Entra ID" toggle marked Production). Save button.
```
