:mod:`os.path` --- Common pathname manipulations
================================================

.. module:: os.path
   :synopsis: Operations on pathnames.

**Source code:** :source:`Lib/posixpath.py`

--------------

This module implements some useful functions on pathnames.

.. function:: join(path, *paths)

   Join one or more path segments intelligently.

.. function:: split(path)

   Split the pathname *path* into a pair ``(head, tail)``.

.. function:: exists(path)

   Return ``True`` if *path* refers to an existing path.
