#  HeartSeek

**A mixed reality hide and seek experience for Meta Quest 3 — where two brothers hide somewhere in your real room, and you find them using only sound and touch.**


---

## What Is This

HeartSeek is a mixed reality game where two twin brothers hide somewhere in your actual room. You can't see them — but you can feel them and hear them.

Your controllers pulse faster as you get closer. Spatial audio drifts from behind your desk, your sofa, your shelf. When you think you know where one is, point your controller and pull the trigger.

The catch: they hide behind **real objects** in your room. Your real furniture physically blocks them. Walk around it — and there they are.

---

## How It Works — The Full Flow

```
App launches
      ↓
Room scanned automatically via Meta Scene API
MRUK maps your furniture, walls and floor
      ↓
Two brothers appear floating in front of you
"We will hide now!" floats above their heads
      ↓
Screen fades to black
Countdown — 3, 2, 1...
Brothers move to hiding positions
behind your real furniture
      ↓
Passthrough fades back in — your room looks normal
They are there — you just can't see them
      ↓
Walk around your room
Controllers vibrate — slow and faint when far
fast and strong when close
Spatial audio plays from their positions
      ↓
Point your controller at a hiding spot
Pull the right trigger
      ↓
Find them both to win
```

---

## The Hiding System

The core technical challenge: **placing characters behind real furniture in a way that is genuinely occluded from the player's starting position.**

### Room Understanding

HeartSeek uses Meta's Mixed Reality Utility Kit (MRUK) to read your room. Every piece of furniture mapped in your Quest's Space Setup becomes a potential hiding place. Each anchor provides world position, orientation, physical dimensions via `VolumeBounds`, and a semantic label.

### Placement Algorithm

For each furniture piece, the algorithm samples positions radially around the furniture perimeter. For each candidate position:

**Floor confirmation**
Raycasts downward from the candidate to find the actual floor surface. If nothing is hit within 2m, or the hit point is more than 20cm from the known floor level, the candidate is discarded. Floor Y comes directly from `room.FloorAnchor.transform.position.y`.

**Room boundary**
`room.IsPositionInRoom()` confirms the candidate is inside the mapped play area.

**Geometry check**
Transforms the candidate into each furniture anchor's local space and checks it against `VolumeBounds` with a small inset margin. Rejects anything inside a furniture mesh. Also runs `Physics.OverlapSphere` to catch mesh collider overlaps that bounds checks might miss.

**Occlusion validation — the key check**
Casts multiple rays from the player's head position to five points around the candidate (top, center, bottom, left, right of the character volume). Counts how many rays are blocked by furniture colliders before reaching the candidate. A spot needs at least 3 of 5 rays blocked to be accepted. Spots with fewer than 3 are visible — rejected.

**Discretion scoring**
Valid spots are ranked by a composite score:
- Direction alignment — is the spot on the far side of the furniture from the player?
- Occlusion ray count — more blocked rays = better hidden
- Wall proximity — spots near walls are harder to approach from behind
- Out of initial sightline — spots outside the player's forward view score higher
- Random variation — so the same spot isn't always chosen

The highest scoring unique spots are assigned to each character. Multiple fallback passes with progressively relaxed separation constraints ensure characters always get placed.

### Depth Occlusion

Characters are physically placed in the room behind real furniture. Quest 3's `OVREnvironmentDepthManager` builds a real-time depth map of the physical world every frame. Virtual pixels behind real surfaces are discarded at the rendering stage — the real furniture hides the virtual character the same way a physical object would.

```
Camera → real desk at 1.2m → depth written
         virtual character at 1.8m
         depth check: 1.2 < 1.8 → pixel discarded
         character invisible ✓

Player walks around desk → no real surface blocking
         character pixel rendered → visible ✓
```

---

## The Feedback Systems

### Haptic Hot and Cold

Both controllers work independently. The vibration on each side is driven by the nearest brother on that side — if one is to your right, your right controller pulses. If you turn around and he's now on your left, the pulse migrates to the left controller. Two brothers on opposite sides means both controllers active simultaneously, each at their own intensity.

The pulse pattern itself encodes distance as temperature:

- **Burning** — continuous strong vibration, no gap. They are right there.
- **Hot** — fast strong pulses, short silence between them. Very close.
- **Warm** — noticeable pulses getting quicker as you approach. Getting warmer.
- **Cool** — slow faint pulses, easy to miss. Keep moving.
- **Cold** — nothing. Too far, change direction.

As you close in, the pulse rate accelerates and the silent gap between pulses shrinks until it disappears entirely into continuous vibration. Turning away drops the intensity immediately — the direction of warmth is always meaningful.

### Spatial Audio

Each brother plays an ambient loop from their exact position in your room. Meta Spatial Audio SDK with HRTF enabled makes the sound genuinely directional — close your eyes and point toward it.

---

## Technical Stack

| Component | Role |
|---|---|
| **Meta Quest 3** | Hardware — color passthrough, depth sensor, 6DOF tracking |
| **Unity 6** | Engine |
| **Meta XR All-in-One SDK** | Core XR framework, controller input, haptics |
| **MRUK (Mixed Reality Utility Kit)** | Room understanding — furniture anchors, floor, walls, room boundary |
| **OVREnvironmentDepthManager** | Real-time depth occlusion — real surfaces hide virtual objects |
| **OVRPassthroughLayer** | Color passthrough — player always sees real room |
| **Meta Spatial Audio SDK** | HRTF directional audio from character world positions |
| **OVRInput haptic API** | `SetControllerVibration()` — directional proximity haptics per controller |
| **Unity Physics** | Raycasting for floor detection, occlusion validation, character discovery |

---



## Design Notes

**Why haptics and audio instead of visual hints?**
Arrows and highlight rings tell you exactly where to look. Removing them makes the physical act of searching — walking around your desk, checking behind your sofa — the actual game.

**Why real furniture?**
Your room is different from everyone else's. Every session is different. The spatial layout of your physical space has real consequence.

**Why passthrough?**
Because the room is the game board. Full VR would cut you off from the physical space you're navigating. Passthrough keeps you grounded while the brothers share your room.

---

## What This Could Become

- **For children** — passthrough keeps a child visually connected to their room and to anyone watching. A parent can see exactly where the child is going and why
- **For multiplayer** — one player hides the brothers via a companion phone app, the other searches. Asymmetric information, shared physical space
- **For accessibility** — the interaction is built on touch and sound, not screen information
- **For research** — haptic guidance, spatial audio, and depth occlusion are independently controllable variables. Each can be toggled to study their individual effect on spatial search behaviour and physical awareness in mixed reality
