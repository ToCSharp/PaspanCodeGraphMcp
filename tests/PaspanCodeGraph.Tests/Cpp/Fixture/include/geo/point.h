#ifndef GEO_POINT_H
#define GEO_POINT_H

namespace geo {

/// A point on the plane.
struct Point {
    double x = 0;
    double y = 0;

    Point operator+(const Point& other) const { return Point{x + other.x, y + other.y}; }
    Point& operator+=(const Point& other);
    bool operator==(const Point&) const = default;
};

/// The distance between two points.
double distance(const Point& a, const Point& b);

inline Point midpoint(const Point& a, const Point& b) { return Point{(a.x + b.x) / 2, (a.y + b.y) / 2}; }

} // namespace geo

#endif
