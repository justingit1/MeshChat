# MeshChat Mockup Specification

## Project Overview
- **Project name**: MeshChat Mockup
- **Type**: Static HTML visual mockup (screenshot-style)
- **Core functionality**: Display a non-interactive UI mockup of a peer-to-peer chat application with limited mesh relay status, optimized for 1200×800px screenshot/printing
- **Target users**: Document/presentation viewers

## UI/UX Specification

### Layout Structure
- **Window dimensions**: 1200×800px, centered on page
- **Window chrome**: Custom title bar with app branding, window controls (minimize, close)
- **Main areas**:
  1. Header bar (48px height) - app title, log button
  2. Chat message area (flexible, ~680px)
  3. Typing indicator area (32px)
  4. Status bar (40px)

### Visual Design

#### Color Palette
- **Background (app)**: `#1a1a2e` (dark navy)
- **Message bubbles (self)**: `#4a6fa5` (muted blue)
- **Message bubbles (others)**: `#2d2d44` (dark purple-gray)
- **Accent color**: `#00d4aa` (teal/cyan)
- **Text primary**: `#ffffff`
- **Text secondary**: `#8b8b9e`
- **Status bar bg**: `#0f0f1a` (darker navy)
- **Header bg**: `#16162a` (dark navy)

#### Avatar Colors (by user)
- Nina: `#ff6b9d` (pink)
- Alex: `#4ecdc4` (teal)
- Sam: `#ffe66d` (yellow)
- Jordan: `#95e1d3` (mint)
- Taylor: `#a78bfa` (purple)

#### Typography
- **Font family**: "Segoe UI", system-ui, sans-serif
- **Header title**: 16px, 600 weight
- **Message sender**: 13px, 600 weight
- **Message text**: 14px, 400 weight
- **Timestamp**: 11px, 400 weight, secondary color
- **Status text**: 12px, 400 weight
- **Typing indicator**: 12px, italic

#### Spacing
- **Message padding**: 12px 16px
- **Message gap**: 8px
- **Avatar size**: 36px (circular)
- **Message bubble radius**: 16px (rounded corners)

### Components

#### Header Bar
- Left: App logo icon (mesh/network symbol) + "MeshChat" title
- Right: "Log" button (outline style), window controls (minimize ╲, close ✕)

#### Message Bubble
- Avatar (circular, colored, initials)
- Sender name (colored to match avatar)
- Message text
- Timestamp (right-aligned)
- File attachments shown as icon + filename

#### Typing Indicator
- "Nina is typing..." with animated dots
- Positioned at bottom of message area

#### Status Bar (two lines)
- Line 1: "Online · Port 45678 · Bluetooth connected"
- Line 2: "Min 2 devices · Limited mesh relay"

## Functionality Specification

### Chat Messages (15+ messages)
Realistic school project conversation about a group assignment:

1. Nina (09:32): "Hey everyone! Just started working on the science project"
2. Alex (09:33): "Nice! What topic did you pick?"
3. Nina (09:33): "Renewable energy sources. I uploaded the outline to the group"
4. [File: project_outline.pdf]
5. Sam (09:35): "Oh this looks great! Should we divide sections?"
6. Jordan (09:36): "I can do solar and wind. Alex, you take hydroelectric?"
7. Alex (09:37): "Sure thing! What about the presentation part?"
8. Nina (09:38): "I'll handle the intro and conclusion"
9. Taylor (09:40): "Can I help with the visual graphs? I'm good with charts"
10. Sam (09:41): "That would be amazing! Here's the data file"
11. [File: energy_data.xlsx]
12. Jordan (09:43): "When's the deadline again?"
13. Nina (09:44): "Next Friday. So we have about a week"
14. Alex (09:45): "Plenty of time. Let's sync up tomorrow after school?"
15. Sam (09:46): "Works for me! Library at 4pm?"
16. All: "👍" reactions from multiple users

### Interactive Elements
- None (static mockup only)

## Acceptance Criteria
1. Window renders at exactly 1200×800px
2. All 15+ messages visible with proper styling
3. Each message has: avatar, sender name, text, timestamp
4. Two file attachments displayed
5. "Nina is typing..." indicator visible at bottom
6. Status bar shows both required lines
7. Window controls (minimize, close) visible in header
8. "Log" button visible in header
9. Colors match specification exactly
10. Clean, professional appearance suitable for printing
