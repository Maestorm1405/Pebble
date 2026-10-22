# Publishing Pebble to the Microsoft Store: step by step

## 1. Developer account (you)
1. Go to https://storedeveloper.microsoft.com, click **Get started**, and sign in with a **personal** Microsoft account.
2. Finish identity verification. It's free for individuals and can take minutes or a couple of days.
3. Once approved, open https://partner.microsoft.com/dashboard.

## 2. Reserve the name (you)
1. **Apps and games → + New product → MSIX or PWA app**.
2. Type **Pebble**. If it's taken, try **Pebble Island** or **Pebble for Windows**.

## 3. Get the package identity (you → Claude)
**Product management → Product identity.** Send Claude these three values:
- `Package/Identity/Name`
- `Package/Identity/Publisher` (starts with `CN=`)
- `Package/Properties/PublisherDisplayName`

Claude then rebuilds the package with:
```
powershell -ExecutionPolicy Bypass -File tools\package.ps1 -IdentityName "<Name>" -Publisher "<CN=...>" -PublisherDisplayName "<Display name>"
```
Output: `artifacts\Pebble_1.0.0.0_x64.msix`

## 4. Fill in the submission (you, with the files in this folder)
| Section | What to enter |
|---|---|
| **Pricing and availability** | Free. Markets: all, or pick yours. Visibility: Public. |
| **Properties** | Category: **Productivity**. Privacy policy URL: see step 5. |
| **Age ratings** | Answer the questionnaire (see `listing.md`). Expect 3+. |
| **Packages** | Upload `artifacts\Pebble_1.0.0.0_x64.msix`. Device family: Desktop. |
| **Submission options → restricted capabilities** | Paste the **runFullTrust** explanation from `listing.md`. |
| **Store listings → English** | Paste the description, features and search terms from `listing.md`. Add screenshots (step 6) and the logo `Assets\Pebble-300.png`. |

## 5. Privacy policy URL (you)
The Store needs a public web page. The simplest free option:
1. Open `privacy-policy.html` in a browser, select all, and copy.
2. Paste it into a new Google Doc → **File → Share → Publish to web → Publish**.
3. Use that link as the privacy policy URL.
(Or ask Claude to set up a free GitHub Pages site.)

## 6. Screenshots
The Store needs at least one desktop screenshot (1366×768 or larger, PNG).
Windows' Smart App Control currently blocks unsigned test builds on this PC, so fresh screenshots of the latest version can't be taken locally yet. Options:
- Use the screenshot Claude prepares from earlier captures for the first submission, then add more in an update once the Store build is installed.
- Or ask a friend without Smart App Control to run the test build and take screenshots.

## 7. Submit
Click **Submit to the Store**. Certification usually takes 1–3 business days. You'll get an email when it's live, or with any changes Microsoft asks for (Claude can help fix those).
