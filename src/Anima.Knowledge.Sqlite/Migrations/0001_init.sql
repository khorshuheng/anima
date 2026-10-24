CREATE TABLE IF NOT EXISTS node_kind (
    id          INTEGER PRIMARY KEY,
    name        TEXT NOT NULL UNIQUE,
    description TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS edge_type (
    id               INTEGER PRIMARY KEY,
    name             TEXT NOT NULL UNIQUE,
    description      TEXT NOT NULL,
    resolves         INTEGER NOT NULL DEFAULT 0,
    single_outgoing  INTEGER NOT NULL DEFAULT 0,
    source_kinds     TEXT NOT NULL DEFAULT '',
    destination_kinds TEXT NOT NULL DEFAULT '',
    updated_at       TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS node (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    kind_id     INTEGER NOT NULL REFERENCES node_kind(id),
    name        TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    content     TEXT NOT NULL DEFAULT '',
    properties  TEXT NOT NULL DEFAULT '{}',
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS edge (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    source       INTEGER NOT NULL REFERENCES node(id) ON DELETE CASCADE,
    destination  INTEGER NOT NULL REFERENCES node(id) ON DELETE CASCADE,
    edge_type_id INTEGER NOT NULL REFERENCES edge_type(id),
    created_at   TEXT NOT NULL,
    UNIQUE (source, edge_type_id, destination)
);

CREATE INDEX IF NOT EXISTS idx_node_kind ON node(kind_id);
CREATE INDEX IF NOT EXISTS idx_node_name ON node(name);
CREATE INDEX IF NOT EXISTS idx_edge_source ON edge(source, edge_type_id);
CREATE INDEX IF NOT EXISTS idx_edge_destination ON edge(destination, edge_type_id);

CREATE VIRTUAL TABLE IF NOT EXISTS node_fts USING fts5(
    name,
    description,
    content,
    properties,
    content = 'node',
    content_rowid = 'id',
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER IF NOT EXISTS node_ai AFTER INSERT ON node BEGIN
    INSERT INTO node_fts(rowid, name, description, content, properties)
    VALUES (new.id, new.name, new.description, new.content, new.properties);
END;

CREATE TRIGGER IF NOT EXISTS node_ad AFTER DELETE ON node BEGIN
    INSERT INTO node_fts(node_fts, rowid, name, description, content, properties)
    VALUES ('delete', old.id, old.name, old.description, old.content, old.properties);
END;

CREATE TRIGGER IF NOT EXISTS node_au AFTER UPDATE ON node BEGIN
    INSERT INTO node_fts(node_fts, rowid, name, description, content, properties)
    VALUES ('delete', old.id, old.name, old.description, old.content, old.properties);
    INSERT INTO node_fts(rowid, name, description, content, properties)
    VALUES (new.id, new.name, new.description, new.content, new.properties);
END;
