import { useEffect, useRef, useState } from 'react';

import { buildTree, type TreeNode } from '../../lib/tree';
import './cameras.css';

/**
 * A single reusable lazily-expanding tree dropdown, shared by the organization-unit and
 * geographic-area registry filters (previously each hand-rolled their own copy, and the clear
 * button in the first copy overlapped the trigger because it was positioned absolutely relative
 * to the wrong ancestor — this version puts the trigger and clear button in one flex row instead,
 * so there's nothing to overlap).
 */
export function TreeSelect<T extends { id: string; name: string; code: string }>({
  id, label, items, getParentId, value, onChange, disabled, loading, error, emptyMessage,
  required, invalid, describedBy, placeholder = 'Any',
}: {
  id: string;
  label: string;
  items: T[] | undefined;
  getParentId: (item: T) => string | null | undefined;
  value: string | undefined;
  onChange(id: string | undefined): void;
  disabled?: boolean;
  loading?: boolean;
  error?: boolean;
  emptyMessage: string;
  required?: boolean;
  invalid?: boolean;
  describedBy?: string;
  placeholder?: string;
}) {
  const [open, setOpen] = useState(false);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const containerRef = useRef<HTMLDivElement>(null);

  const selected = items?.find((item) => item.id === value);
  const tree = items ? buildTree(items, getParentId) : [];
  const labelId = `${id}-label`;

  useEffect(() => {
    if (!open) return undefined;
    function onPointerDown(event: PointerEvent) {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false);
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') setOpen(false);
    }
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  function toggleExpanded(nodeId: string) {
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(nodeId)) next.delete(nodeId);
      else next.add(nodeId);
      return next;
    });
  }

  function select(item: T) {
    onChange(item.id);
    setOpen(false);
  }

  function renderNode(node: TreeNode<T>) {
    const isExpanded = expanded.has(node.item.id);
    const hasChildren = node.children.length > 0;
    return (
      <li key={node.item.id}>
        <div className="tree-select__row" style={{ paddingLeft: `${node.depth * 1.25}rem` }}>
          {hasChildren ? (
            <button
              aria-expanded={isExpanded}
              aria-label={isExpanded ? `Collapse ${node.item.name}` : `Expand ${node.item.name}`}
              className="tree-select__toggle"
              type="button"
              onClick={() => toggleExpanded(node.item.id)}
            >
              <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className={isExpanded ? 'tree-select__chevron tree-select__chevron--open' : 'tree-select__chevron'}>
                <path d="m9 6 6 6-6 6" />
              </svg>
            </button>
          ) : <span className="tree-select__toggle-spacer" aria-hidden="true" />}
          <button
            aria-current={node.item.id === value ? 'true' : undefined}
            className="tree-select__label"
            type="button"
            onClick={() => select(node.item)}
          >
            {node.item.name} <span className="tree-select__code">({node.item.code})</span>
          </button>
        </div>
        {hasChildren && isExpanded && <ul className="tree-select__list">{node.children.map(renderNode)}</ul>}
      </li>
    );
  }

  return (
    <div className="tree-select" ref={containerRef}>
      <span className="tree-select__field-label" id={labelId}>{label}</span>
      <div className="tree-select__control">
        <button
          id={id}
          role="combobox"
          aria-expanded={open}
          aria-controls={`${id}-panel`}
          aria-haspopup="tree"
          aria-labelledby={labelId}
          aria-describedby={describedBy}
          aria-invalid={invalid}
          aria-required={required}
          className="tree-select__trigger"
          disabled={disabled}
          type="button"
          onClick={() => setOpen((current) => !current)}
        >
          {selected ? `${selected.name} (${selected.code})` : placeholder}
        </button>
        {value && <button className="tree-select__clear" type="button" aria-label={`Clear ${label} filter`} onClick={() => onChange(undefined)}>×</button>}
      </div>
      {open && (
        <div id={`${id}-panel`} className="tree-select__panel" role="tree" aria-label={label}>
          {loading ? <p className="tree-select__hint">Loading…</p>
            : error ? <p className="tree-select__hint">Could not be loaded.</p>
              : tree.length === 0 ? <p className="tree-select__hint">{emptyMessage}</p>
                : <ul className="tree-select__list">{tree.map(renderNode)}</ul>}
        </div>
      )}
    </div>
  );
}
