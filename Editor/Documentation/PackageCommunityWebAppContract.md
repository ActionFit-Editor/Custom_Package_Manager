# Package Community Web App Contract

Custom Package Manager package voting and comments use the existing catalog Web App URL in `ActionFitPackageCatalogSettings_SO`.

The Unity client stores an anonymous project community ID in `UserSettings/ActionFitPackageManager/community_id.txt`. This file is ignored by the project and is not a user account, email, machine name, or Git identity. The Web App should treat `package_id + vote_id` as the unique key for duplicate prevention.

## Sheets

Add these sheets to the same spreadsheet used by the package catalog.

### `package_votes`

| column | notes |
| --- | --- |
| `package_id` | Package ID such as `com.actionfit.csvimporter`. |
| `vote_id` | Anonymous project ID from Unity. |
| `vote` | `like` or `dislike`. |
| `created_at` | First vote timestamp. |
| `updated_at` | Last vote timestamp. |

The Web App must treat `package_id + vote_id` as a single final vote. If a vote row already exists, leave the stored `vote` unchanged and return the current `my_vote` and summary counts. Repeated equal votes and attempted changes from `like` to `dislike`, or the reverse, are no-ops.

### `package_comments`

| column | notes |
| --- | --- |
| `comment_id` | Stable ID. `package_id + vote_id` is enough for one editable comment per project/package. |
| `package_id` | Package ID. |
| `vote_id` | Anonymous project ID from Unity. |
| `title` | Comment title. |
| `body` | Comment description. |
| `created_at` | First comment timestamp. |
| `updated_at` | Last edit timestamp. |
| `hidden` | Optional moderation flag. Hidden rows should not be returned to Unity. |

The Web App must upsert by `package_id + vote_id`. The first implementation intentionally keeps one editable comment per project/package.

### `package_vote_summary`

This sheet can be computed by Apps Script or spreadsheet formulas and is optional in Web App fetch responses.

| column | notes |
| --- | --- |
| `package_id` | Package ID. |
| `likes` | Count of `like` rows. |
| `dislikes` | Count of `dislike` rows. |
| `vote_score` | `likes - dislikes`. |
| `comment_count` | Count of visible comments. |

When `Update Catalog` receives this sheet, the local catalog CSV includes `likes`, `dislikes`, `vote_score`, and `comment_count`.

### `Update Catalog` comment cache

The catalog fetch response should include the `package_comments` sheet together with `package_catalog`, `package_versions`, and optional `package_vote_summary`.

Unity stores the returned comments at `UserSettings/ActionFitPackageManager/package_comments.csv` and reads that cache in the Package Manager window. There is no per-package comment fetch button; users refresh all visible package comments through `Update Catalog`.

Only visible comments should be returned. If hidden rows are returned, Unity filters rows where `hidden` is `true`, `1`, or `yes`.

## Community Actions

Community POST bodies include `token`, `action`, `ssId`, `package_id`, and `vote_id`.

### `votePackage`

Request:

```json
{
  "token": "configured-token",
  "action": "votePackage",
  "ssId": "spreadsheet-id",
  "package_id": "com.actionfit.csvimporter",
  "vote_id": "anonymous-project-id",
  "vote": "like"
}
```

Response:

```json
{
  "success": true,
  "package_id": "com.actionfit.csvimporter",
  "my_vote": "like",
  "likes": 12,
  "dislikes": 3,
  "comment_count": 4
}
```

## Catalog Publish Actions

Catalog publish POST bodies include `token`, `action`, `ssId`, and catalog payload data. The existing single-row action remains supported so older Web App deployments can continue to work.

### `upsertPackageVersion`

Request:

```json
{
  "token": "configured-token",
  "action": "upsertPackageVersion",
  "ssId": "spreadsheet-id",
  "package": {
    "package_id": "com.actionfit.csvimporter",
    "display_name": "CSV Importer",
    "repo_url": "https://github.com/ActionFit-Editor/CSV_Importer.git",
    "owner": "owner",
    "status": "verified",
    "description": "description"
  },
  "version": {
    "catalog_id": "com.actionfit.csvimporter@1.4.15",
    "version": "1.4.15",
    "unity_min": "6000.2",
    "changelog": "release note",
    "dependencies": "{}"
  }
}
```

Response:

```json
{
  "success": true,
  "package_id": "com.actionfit.csvimporter",
  "version": "1.4.15",
  "catalog_id": "com.actionfit.csvimporter@1.4.15"
}
```

### `upsertPackageVersions`

`Publish All Changed` sends this batch action after all repository publishes succeed. The Web App should upsert every item in one request and return either `count` matching the request item count or per-item confirmations. If the Unity client receives an unsupported response contract, it falls back to serial `upsertPackageVersion`. Requests use a 30-second connection/read timeout; timeout or user cancellation retains retryable catalog rows and skips serial fallback because the batch mutation outcome may be ambiguous.

Request:

```json
{
  "token": "configured-token",
  "action": "upsertPackageVersions",
  "ssId": "spreadsheet-id",
  "items": [
    {
      "package": {
        "package_id": "com.actionfit.csvimporter",
        "display_name": "CSV Importer",
        "repo_url": "https://github.com/ActionFit-Editor/CSV_Importer.git",
        "owner": "owner",
        "status": "verified",
        "description": "description"
      },
      "version": {
        "catalog_id": "com.actionfit.csvimporter@1.4.15",
        "version": "1.4.15",
        "unity_min": "6000.2",
        "changelog": "release note",
        "dependencies": "{}"
      }
    }
  ]
}
```

Response:

```json
{
  "success": true,
  "count": 1,
  "items": [
    {
      "success": true,
      "package_id": "com.actionfit.csvimporter",
      "version": "1.4.15",
      "catalog_id": "com.actionfit.csvimporter@1.4.15"
    }
  ]
}
```

### `upsertPackageComment`

Request:

```json
{
  "token": "configured-token",
  "action": "upsertPackageComment",
  "ssId": "spreadsheet-id",
  "package_id": "com.actionfit.csvimporter",
  "vote_id": "anonymous-project-id",
  "title": "Useful importer",
  "body": "Comment body"
}
```

Response:

```json
{
  "success": true,
  "package_id": "com.actionfit.csvimporter",
  "likes": 12,
  "dislikes": 3,
  "comment_count": 4
}
```

## Client Behavior

- Package sections are sorted by `likes - dislikes`, highest first.
- The same project can cast one final vote per package. It cannot switch between `like` and `dislike` after the first successful vote.
- The same project can keep one editable comment per package.
- Comment titles are shown first; the body is displayed through a foldout per title.
- Package comments are refreshed in bulk by `Update Catalog`. The Package Manager window does not show per-package comment load buttons.
- If the Web App does not support the community actions yet, Unity shows the Web App response in the package `Community` panel.
