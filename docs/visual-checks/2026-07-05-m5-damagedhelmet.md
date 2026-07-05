# Protocole visuel M5 — DamagedHelmet (spec §5)

**Statut : PASS (revue humaine du 2026-07-05)**

## Déroulé effectif

La comparaison côte à côte avec le viewer Khronos (Debug Channels : Normal Texture, Geometry
Normal, Base Color, Metallic, Roughness, Geometry Tangent) a révélé un **vrai bug** : la coque
externe du casque manquait, exposant la structure interne et le HUD (« on voit l'intérieur du
modèle »). Diagnostic par captures headless (`AGAPANTHE_CAPTURE`/`AGAPANTHE_VIEW`, outillage créé
pour l'occasion) : le back-face culling supprimait les faces avant — `FrontFace.Clockwise` était
faux (signe manquant dans la dérivation du winding vs la formule Vulkan). Corrigé en
`CounterClockwise` (commit `0dd9ff1`), casque fermé identique au viewer Khronos, validé par
l'utilisateur (« nikel »).

Les vues debug moteur (touche N) correspondent aux Debug Channels du viewer Khronos :
normales/baseColor/metallic/roughness/tangentes cohérentes des deux côtés.

Écart résiduel attendu : pas d'IBL avant M7 → zones hors éclairage direct plus sombres que le
viewer (ambiant constant + plancher spéculaire f0 pour les métaux).

## Procédure

1. **Agapanthe** : `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox`
   - Cadrage par défaut (ne pas bouger la caméra), exposition par défaut (1.0 = +0.0 EV).
   - Capture d'écran → `m5-damagedhelmet-agapanthe.png` dans ce dossier.
2. **Référence Khronos** : https://github.khronos.org/glTF-Sample-Viewer-Release/
   - Charger DamagedHelmet (menu Models), tonemap **ACES** (menu Advanced Controls),
     désactiver l'IBL (Punctual only) si possible pour comparer à éclairage équivalent,
     sinon noter la différence d'ambiance.
   - Orienter approximativement au même angle que le Sandbox.
   - Capture → `m5-damagedhelmet-khronos.png`.
3. Comparer côte à côte et annoter ci-dessous.

## Critères (cocher)

| Critère | Attendu | OK ? |
|---|---|---|
| Métal vs diélectrique | Les zones métalliques (casque) réfléchissent teinté, sans diffus ; les zones mates (caoutchouc, visière) ont un diffus dominant | ☐ |
| Roughness | Les rayures/usure accrochent le spéculaire différemment des zones lisses | ☐ |
| Normal mapping | Le relief fin (bosses, gravures) réagit à la lumière — tourner la key light avec L pour vérifier | ☐ |
| Fresnel | Reflets plus intenses en incidence rasante (bords de la sphère du casque) | ☐ |
| AO | Les recoins (autour des lunettes, sous les sangles) sont assombris | ☐ |
| Emissive | Rien d'anormal (le helmet n'a pas d'emissive fort) | ☐ |
| Tone mapping | Pas de saturation blanche plate sur les hautes lumières ; les dégradés roulent doucement | ☐ |
| Pas d'artefacts | Pas de NaN (pixels noirs/blancs isolés), pas de seams de tangentes, pas d'acné | ☐ |

## Écarts attendus vs viewer Khronos

- **Pas d'IBL avant M7** : notre ambiant est une constante 0.03 — les zones non éclairées
  directement seront plus sombres/plates que le viewer (qui a de l'IBL par défaut).
- Lumières différentes (notre 3-points vs l'éclairage du viewer) : comparer le *caractère*
  des matériaux, pas l'ambiance globale.

## Verdict

_(à remplir après capture : PASS / FAIL + observations)_
