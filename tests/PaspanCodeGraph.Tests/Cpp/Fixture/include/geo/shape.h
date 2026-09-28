#pragma once
#include "geo/point.h"

namespace geo {

class Visitor;

/// A shape on the plane.
class Shape {
public:
    explicit Shape(const char* name);
    virtual ~Shape();

    /// The area of the shape.
    virtual double area() const = 0;
    virtual double perimeter() const { return 0; }
    virtual void accept(Visitor& visitor) const;

    const char* name() const { return name_; }
    void move(const Point& by);
    void move(double dx, double dy);
    Point origin() const { return origin_; }

    static int count;

protected:
    Point origin_;

private:
    const char* name_;
};

} // namespace geo
